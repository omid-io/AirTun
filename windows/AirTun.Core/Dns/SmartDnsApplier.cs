using System.Diagnostics;
using System.Net.NetworkInformation;
using AirTun.Core.Resolvers;

namespace AirTun.Core.Resolvers;

/// <summary>Where/how DNS was last applied, for UI display and Unset.</summary>
public sealed record DnsApplyTarget(string AdapterName, string Mode);

/// <summary>
/// Applies the selected resolver to Windows:
///  • TUN running      → nothing to set on adapters; tunnel's internal forwarder already
///                       uses the chosen DoH (passed as -doh at start). Returns mode=tunnel.
///  • TUN not running  → netsh sets primary/secondary on the adapter that owns the
///                       default route (Ethernet/Wi-Fi). Unset restores DHCP DNS.
/// </summary>
public static class SmartDnsApplier
{
    public static DnsApplyTarget? Current { get; private set; }

    private static string FindDefaultAdapter()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (nic.Name.Equals("AirTun", StringComparison.OrdinalIgnoreCase)) continue;
            if (nic.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                return nic.Name;
        }
        // fallback: first up non-virtual-ish adapter
        return NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
                                 n.NetworkInterfaceType != NetworkInterfaceType.Loopback)?.Name ?? "Ethernet";
    }

    /// <summary>Runs netsh, returns success.</summary>
    private static bool RunNetsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(8000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Apply the active selection.
    /// tunRunning: tunnel owns DNS via its forwarder — no adapter change needed.
    /// </summary>
    public static (bool ok, string message) Apply(DnsServer server, bool tunRunning)
    {
        if (tunRunning)
        {
            Current = new DnsApplyTarget("(AirTun tunnel)", "tunnel");
            return (true, "Tunnel active — its built-in DoH forwarder uses this resolver.");
        }

        var adapter = FindDefaultAdapter();
        if (server.Kind == "system")
        {
            var ok = RunNetsh($"interface ip set dnsservers name=\"{adapter}\" source=dhcp");
            Current = ok ? new DnsApplyTarget(adapter, "dhcp") : null;
            return ok ? (true, $"Restored DHCP DNS on {adapter}.") : (false, "netsh failed (run elevated?)");
        }

        var ok1 = RunNetsh($"interface ip set dnsservers name=\"{adapter}\" static {server.Primary} primary");
        if (!ok1) return (false, "netsh failed — run AirTun as Administrator to set system DNS.");

        if (!string.IsNullOrWhiteSpace(server.Secondary))
            RunNetsh($"interface ip add dnsservers name=\"{adapter}\" {server.Secondary} index=2");

        Current = new DnsApplyTarget(adapter, server.Kind);
        FlushCache();
        return (true, $"DNS applied on {adapter}.");
    }

    /// <summary>Restore DHCP-managed DNS on the previously-touched adapter.</summary>
    public static (bool ok, string message) Unset()
    {
        var adapter = Current?.AdapterName is { Length: > 0 } a && !a.StartsWith("(") ? a : FindDefaultAdapter();
        var ok = RunNetsh($"interface ip set dnsservers name=\"{adapter}\" source=dhcp");
        if (ok) { Current = null; FlushCache(); }
        return ok ? (true, $"DHCP restored on {adapter}.") : (false, "netsh failed.");
    }

    public static void FlushCache()
    {
        try
        {
            var psi = new ProcessStartInfo("ipconfig", "/flushdns")
            { CreateNoWindow = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            p.WaitForExit(4000);
        }
        catch { }
    }
}
