using AirTun.Core;
using AirTun.Core.Geo;
using AirTun.Core.Proxy;
using AirTun.Core.Routing;
using AirTun.Core.Settings;
using AirTun.Core.Tunnel;

namespace AirTun.App.Services;

public sealed class AppController : IDisposable
{
    private readonly LanDiscovery _discovery = new();
    private readonly ProxySession _proxySession;
    private readonly WinTunTunnelSession _tunSession;
    private readonly TunnelStats _stats = new();
    private CancellationTokenSource? _statsTimerCts;

    public ConnectionState State { get; private set; } = ConnectionState.Idle;
    public string ActiveMode { get; set; } = "tun";

    public RoutingManager Routing { get; } = new();
    public TunRoutingManager TunRouting { get; } = new();
    public GeoIpService GeoIp { get; } = new();
    public AppSettings Settings { get; private set; } = new();
    public GeoIpInfo? CurrentGeo { get; private set; }

    public event Action<ConnectionState>? StateChanged;
    public event Action<IReadOnlyList<LanDiscovery.Device>>? DevicesChanged;
    public event Action<TunnelStats.Sample>? StatsSampled;
    public event Action<GeoIpInfo?>? GeoLocationUpdated;

    public AppController()
    {
        _proxySession = new ProxySession(new WinInetProxyStore(), new FileBackupStore());
        var tunExe = Path.Combine(AppContext.BaseDirectory, "airtun-tun.exe");
        _tunSession = new WinTunTunnelSession(new ElevatedTunnelProcessHost(tunExe));

        _discovery.DevicesChanged += devices => DevicesChanged?.Invoke(devices);
        _discovery.DiagnosticLog += msg => LocalLog.Discovery(msg);
        TunRouting.LogGenerated += msg => LocalLog.Add(msg);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { TunRouting.PurgeAllRoutes(); } catch { }
            try { _proxySession.Disconnect(); } catch { }
        };

        LoadSavedSettings();
    }

    private void LoadSavedSettings()
    {
        Settings = AppSettings.Load();
        Routing.BypassDomestic = Settings.BypassDomestic;
        Routing.BypassLan = Settings.BypassLan;
        foreach (var rule in Settings.CustomRules)
        {
            Routing.CustomRules.Add(rule);
        }
    }

    public void SaveCurrentSettings()
    {
        Settings.BypassDomestic = Routing.BypassDomestic;
        Settings.BypassLan = Routing.BypassLan;
        Settings.CustomRules = [.. Routing.CustomRules];
        Settings.Save();
    }

    public void StartDiscovery()
    {
        _discovery.Start();
        _discovery.SetProbing(true);
        LocalLog.Discovery("Discovery started on port " + LanDiscovery.Port);
    }

    public void StopDiscovery()
    {
        _discovery.SetProbing(false);
    }

    public async Task<bool> ConnectAsync(string host, int port, string pinCode, string deviceName)
    {
        if (State is not (ConnectionState.IdleState or ConnectionState.DiscoveringState or ConnectionState.ErrorState))
            return false;

        Transition(ConnectionState.Preparing);
        LocalLog.Info($"Connecting to {host}:{port} ({ActiveMode} mode) with PIN {pinCode}...");

        if (ActiveMode == "proxy")
        {
            var bypassList = Routing.BuildWinInetBypassList();
            LocalLog.Info($"Applying system proxy with {bypassList.Split(';').Length} bypass entries...");
            var res = await Task.Run(() => _proxySession.Connect(host, port, bypassList));
            if (!res.Ok)
            {
                LocalLog.Error($"Proxy connect failed: {res.ErrorCode}");
                Transition(new ConnectionState.ErrorState(ErrorCode.PortInUse, res.ErrorCode));
                return false;
            }
        }
        else
        {
            LocalLog.Tun($"Starting Wintun tunnel session to {host}:{port}...");
            var res = await Task.Run(() => _tunSession.Connect(host, port, pinCode));
            if (!res.Ok)
            {
                LocalLog.Error($"TUN connect failed: {res.ErrorCode}");
                Transition(new ConnectionState.ErrorState(ErrorCode.TunnelFailed, res.ErrorCode));
                return false;
            }

            LocalLog.Tun("Wintun tunnel adapter connected.");
            // Apply TUN bypass routes (Iranian GeoIP CIDRs, LAN RFC1918, Custom Rules)
            await TunRouting.ApplyTunBypassRoutesAsync(
                gateway: null,
                bypassDomestic: Routing.BypassDomestic,
                bypassLan: Routing.BypassLan,
                customRules: Routing.CustomRules
            ).ConfigureAwait(false);
        }

        Transition(new ConnectionState.ConnectedState(
            Host: host,
            Port: port,
            PinCode: pinCode,
            DeviceName: deviceName,
            Mode: ActiveMode
        ));

        StartStatsPolling(host, port);
        _ = RefreshGeoLocationAsync();
        LocalLog.Info("Connected successfully!");
        return true;
    }

    public async Task SetBypassDomesticAsync(bool enabled)
    {
        Routing.BypassDomestic = enabled;
        SaveCurrentSettings();

        if (State is ConnectionState.ConnectedState connected)
        {
            if (connected.Mode == "tun")
            {
                await TunRouting.SetDomesticBypassAsync(enabled).ConfigureAwait(false);
            }
            else if (connected.Mode == "proxy")
            {
                var bypassList = Routing.BuildWinInetBypassList();
                _proxySession.Connect(connected.Host, connected.Port, bypassList);
                LocalLog.Routing($"System proxy updated. Domestic bypass: {enabled}");
            }
        }
    }

    public async Task SetBypassLanAsync(bool enabled)
    {
        Routing.BypassLan = enabled;
        SaveCurrentSettings();

        if (State is ConnectionState.ConnectedState connected)
        {
            if (connected.Mode == "tun")
            {
                await TunRouting.SetLanBypassAsync(enabled).ConfigureAwait(false);
            }
            else if (connected.Mode == "proxy")
            {
                var bypassList = Routing.BuildWinInetBypassList();
                _proxySession.Connect(connected.Host, connected.Port, bypassList);
                LocalLog.Routing($"System proxy updated. LAN bypass: {enabled}");
            }
        }
    }

    public async Task AddCustomRuleAsync(RoutingRule rule)
    {
        Routing.AddCustomRule(rule);
        SaveCurrentSettings();

        if (State is ConnectionState.ConnectedState connected)
        {
            if (connected.Mode == "tun")
            {
                await TunRouting.AddCustomRuleAsync(rule).ConfigureAwait(false);
            }
            else if (connected.Mode == "proxy")
            {
                var bypassList = Routing.BuildWinInetBypassList();
                _proxySession.Connect(connected.Host, connected.Port, bypassList);
                LocalLog.Routing($"System proxy updated with rule: {rule.Pattern}");
            }
        }
        else
        {
            LocalLog.Routing($"Added custom direct bypass rule: {rule.Pattern}");
        }
    }

    public void RemoveCustomRule(RoutingRule rule)
    {
        Routing.RemoveCustomRule(rule);
        SaveCurrentSettings();

        if (State is ConnectionState.ConnectedState connected)
        {
            if (connected.Mode == "tun")
            {
                TunRouting.RemoveCustomRule(rule);
            }
            else if (connected.Mode == "proxy")
            {
                var bypassList = Routing.BuildWinInetBypassList();
                _proxySession.Connect(connected.Host, connected.Port, bypassList);
                LocalLog.Routing($"System proxy updated after removing rule: {rule.Pattern}");
            }
        }
        else
        {
            LocalLog.Routing($"Removed rule: {rule.Pattern}");
        }
    }

    public async Task RefreshGeoLocationAsync()
    {
        try
        {
            LocalLog.Info("Resolving outbound location and IP...");
            string? proxyHost = null;
            int? proxyPort = null;
            if (State is ConnectionState.ConnectedState connected)
            {
                proxyHost = connected.Host;
                proxyPort = connected.Port;
            }
            var geo = await GeoIp.FetchOutboundGeoAsync(proxyHost, proxyPort).ConfigureAwait(false);
            CurrentGeo = geo;
            if (geo is not null)
            {
                LocalLog.Info($"Outbound IP: {geo.Ip} ({geo.Country} {geo.FlagEmoji}) - ISP: {geo.Isp}");
            }
            GeoLocationUpdated?.Invoke(geo);
        }
        catch (Exception ex)
        {
            LocalLog.Error($"GeoIP resolution failed: {ex.Message}");
        }
    }

    public void Disconnect()
    {
        LocalLog.Info("Disconnecting...");
        StopStatsPolling();
        CurrentGeo = null;
        GeoLocationUpdated?.Invoke(null);

        try { TunRouting.PurgeAllRoutes(); } catch { }
        try { _proxySession.Disconnect(); } catch { }
        try { _tunSession.Disconnect(); } catch { }

        Transition(ConnectionState.Idle);
        LocalLog.Info("Disconnected.");
    }

    private void StartStatsPolling(string host, int port)
    {
        _statsTimerCts?.Cancel();
        _statsTimerCts = new CancellationTokenSource();
        var token = _statsTimerCts.Token;

        _ = Task.Run(async () =>
        {
            await Task.Delay(500, token).ConfigureAwait(false);
            var (baseUp, baseDown) = ReadAirTunInterfaceBytes();

            while (!token.IsCancellationRequested)
            {
                // If in TUN mode and the underlying Wintun session process has exited, trigger cleanup
                if (ActiveMode == "tun" && !_tunSession.IsRunning)
                {
                    LocalLog.Error("Wintun tunnel process exited unexpectedly. Disconnecting...");
                    _ = Task.Run(() => Disconnect());
                    break;
                }

                var (rawUp, rawDown) = ReadAirTunInterfaceBytes();
                if (baseUp == 0 && rawUp > 0) baseUp = rawUp;
                if (baseDown == 0 && rawDown > 0) baseDown = rawDown;

                long curBytesUp = Math.Max(0, rawUp - baseUp);
                long curBytesDown = Math.Max(0, rawDown - baseDown);

                var ping = await _stats.MeasurePingAsync(host, 1200).ConfigureAwait(false);
                var sample = _stats.ComputeSample(curBytesUp, curBytesDown, ping > 0 ? ping : 18);
                StatsSampled?.Invoke(sample);

                try { await Task.Delay(1000, token).ConfigureAwait(false); }
                catch { break; }
            }
        }, token);
    }

    private static (long bytesSent, long bytesRecv) ReadAirTunInterfaceBytes()
    {
        try
        {
            var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Name.Contains("AirTun", StringComparison.OrdinalIgnoreCase)
                                  || n.Description.Contains("AirTun", StringComparison.OrdinalIgnoreCase)
                                  || n.Description.Contains("tun2socks", StringComparison.OrdinalIgnoreCase));
            if (nic != null)
            {
                var stats = nic.GetIPv4Statistics();
                return (stats.BytesSent, stats.BytesReceived);
            }
        }
        catch { }
        return (0, 0);
    }

    private void StopStatsPolling()
    {
        _statsTimerCts?.Cancel();
        _statsTimerCts?.Dispose();
        _statsTimerCts = null;
    }

    private void Transition(ConnectionState newState)
    {
        State = newState;
        StateChanged?.Invoke(newState);
    }

    public void RecoverOnStartup()
    {
        if (_proxySession.RecoverIfCrashed())
        {
            LocalLog.Info("Recovered proxy settings from previous ungraceful exit.");
        }
        try { TunRouting.PurgeAllRoutes(); } catch { }
    }

    public void Dispose()
    {
        Disconnect();
        _discovery.Dispose();
    }
}
