using System.Diagnostics;
using System.IO.Pipes;
using AirTun.Core.Resolvers;

namespace AirTun.Core.Tunnel;

public sealed class WinTunTunnelSession(WinTunTunnelSession.IProcessHost processHost)
{
    public sealed record Result(bool Ok, string? ErrorCode = null)
    {
        public static readonly Result Success = new(true);
        public static Result Fail(string code) => new(false, code);
    }

    public interface IProcessHost
    {
        IProcessHandle Start(string arguments);
    }

    public interface IProcessHandle : IDisposable
    {
        void WriteLine(string line);
        string? ReadLine();
        void CloseInput();
        bool WaitForExit(TimeSpan timeout);
        void Kill();
        bool HasExited { get; }
    }

    public sealed class ElevationDeclined(Exception? inner = null)
        : Exception("The elevation prompt was declined", inner);

    public const string ReadyLine = "READY";
    public const string NoHandshakeLine = "NO-HANDSHAKE";
    public const string ConfigTerminator = "END-CONFIG";
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(40);

    private IProcessHandle? _tunnel;

    public bool IsRunning => _tunnel is { HasExited: false };

    public Result Connect(string host, int port, string pinCode)
    {
        if (IsRunning) return Result.Fail("ERR_TUNNEL_ALREADY_RUNNING");

        IProcessHandle tunnel;
        try
        {
            var args = $"-proxy socks5://airtun:{pinCode}@{host}:{port} -tun-name AirTun -tun-addr 10.254.1.2/24";
            // Forward the user-selected DoH endpoint so the tunnel's DNS forwarder
            // resolves through the chosen resolver (DNS tab selection), not a fixed one.
            var doh = DnsSelectionStore.GetActiveDohUrl();
            if (!string.IsNullOrWhiteSpace(doh))
                args += $" -doh \"{doh}\"";
            tunnel = processHost.Start(args);
        }
        catch (ElevationDeclined)
        {
            return Result.Fail("ERR_ELEVATION_DECLINED");
        }
        catch (Exception ex)
        {
            return Result.Fail($"ERR_TUNNEL_START_FAILED ({ex.Message})");
        }

        var failure = WaitForReady(tunnel);
        if (failure is not null)
        {
            Stop(tunnel);
            return Result.Fail(failure);
        }

        _tunnel = tunnel;
        return Result.Success;
    }

    public Result Disconnect()
    {
        var tunnel = _tunnel;
        _tunnel = null;
        if (tunnel is null) return Result.Success;

        return Stop(tunnel) ? Result.Success : Result.Fail("ERR_TUNNEL_STOP_FAILED");
    }

    private static bool Stop(IProcessHandle tunnel)
    {
        try
        {
            if (!tunnel.HasExited)
            {
                tunnel.CloseInput();
                if (!tunnel.WaitForExit(TimeSpan.FromSeconds(5)))
                {
                    tunnel.Kill();
                    if (!tunnel.WaitForExit(TimeSpan.FromSeconds(3))) return false;
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { tunnel.Dispose(); } catch { }
        }
    }

    private static string? WaitForReady(IProcessHandle tunnel)
    {
        var deadline = DateTimeOffset.UtcNow + ReadyTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var line = tunnel.ReadLine();
            if (line is null) return "ERR_TUNNEL_START_FAILED";
            var trimmed = line.Trim();
            if (trimmed == ReadyLine) return null;
            if (trimmed == NoHandshakeLine) return "ERR_TUNNEL_AUTH_FAILED";
        }
        return "ERR_TUNNEL_START_FAILED";
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ElevatedTunnelProcessHost(string executablePath) : WinTunTunnelSession.IProcessHost
{
    private const int ErrorCancelled = 1223;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    public WinTunTunnelSession.IProcessHandle Start(string arguments)
    {
        var pipeName = "airtun-tun-" + Guid.NewGuid().ToString("N");

        var pipeSecurity = new System.IO.Pipes.PipeSecurity();
        pipeSecurity.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
            System.IO.Pipes.PipeAccessRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow
        ));

        var pipe = System.IO.Pipes.NamedPipeServerStreamAcl.Create(
            pipeName,
            System.IO.Pipes.PipeDirection.InOut,
            1,
            System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            pipeSecurity
        );

        Process process;
        try
        {
            var startInfo = new ProcessStartInfo(executablePath, $"{arguments} -pipe {pipeName}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Process failed to start");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            pipe.Dispose();
            throw new WinTunTunnelSession.ElevationDeclined(ex);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }

        // Wait for elevated child to connect to our pipe
        try
        {
            var connectionTask = pipe.WaitForConnectionAsync();
            var deadline = DateTimeOffset.UtcNow + ConnectTimeout;
            while (!connectionTask.Wait(TimeSpan.FromMilliseconds(200)))
            {
                if (process.HasExited)
                    throw new InvalidOperationException($"Tunnel process exited with code {process.ExitCode}");
                if (DateTimeOffset.UtcNow > deadline)
                    throw new TimeoutException("Timed out waiting for tunnel pipe connection");
            }
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            pipe.Dispose();
            process.Dispose();
            throw;
        }

        return new TunnelProcessHandle(process, pipe);
    }

    private sealed class TunnelProcessHandle(Process process, System.IO.Pipes.NamedPipeServerStream pipe)
        : WinTunTunnelSession.IProcessHandle
    {
        private readonly StreamWriter _writer = new(pipe) { AutoFlush = true, NewLine = "\n" };
        private readonly StreamReader _reader = new(pipe);

        public bool HasExited => process.HasExited;
        public void WriteLine(string line) => _writer.WriteLine(line);

        public string? ReadLine()
        {
            try
            {
                return _reader.ReadLine();
            }
            catch
            {
                return null;
            }
        }

        public void CloseInput()
        {
            try { _writer.Close(); } catch { }
        }

        public bool WaitForExit(TimeSpan timeout) => process.WaitForExit((int)timeout.TotalMilliseconds);
        public void Kill() => process.Kill(entireProcessTree: true);

        public void Dispose()
        {
            try { pipe.Dispose(); } catch { }
            try { process.Dispose(); } catch { }
        }
    }
}
