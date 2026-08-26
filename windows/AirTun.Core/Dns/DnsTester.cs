using System.Diagnostics;
using System.Net;

namespace AirTun.Core.Resolvers;

/// <summary>Result of one DNS health test run.</summary>
public sealed record DnsTestResult(
    bool Success,
    int LatencyMs,
    string? ResolvedIp,
    string? Error
);

/// <summary>
/// Runs a 3-stage health test against a candidate resolver:
/// 1) latency (median of 3 A-queries for a well-known host)
/// 2) resolution correctness (got an A record back)
/// 3) bypass-check: does this resolver return a *different* IP for gemini.google.com
///    than the system default? (anti-sanction resolvers relay sanctioned domains)
/// </summary>
public static class DnsTester
{
    /// <summary>Resolve `host` via UDP to `dnsIp` with a timeout; returns first IPv4 or null.</summary>
    private static (IPAddress? ip, int ms) UdpQuery(string dnsIp, string host, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new System.Net.Sockets.UdpClient(dnsIp, 53);
            client.Client.ReceiveTimeout = timeoutMs;
            var query = BuildDnsQuery(host);
            client.Send(query, query.Length);

            var ep = new IPEndPoint(IPAddress.Any, 0);
            var response = client.Receive(ref ep);
            sw.Stop();
            return (ParseFirstARecord(response), (int)sw.ElapsedMilliseconds);
        }
        catch { sw.Stop(); return (null, (int)sw.ElapsedMilliseconds); }
    }

    private static byte[] BuildDnsQuery(string host)
    {
        // Header: ID=0x1234, RD=1, QDCOUNT=1
        var parts = host.Split('.');
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
        foreach (var p in parts)
        {
            ms.Write(new[] { (byte)p.Length });
            var b = System.Text.Encoding.ASCII.GetBytes(p);
            ms.Write(b);
        }
        ms.Write(new byte[] { 0 });           // root
        ms.Write(new byte[] { 0x00, 0x01 });  // QTYPE=A
        ms.Write(new byte[] { 0x00, 0x01 });  // QCLASS=IN
        return ms.ToArray();
    }

    private static IPAddress? ParseFirstARecord(byte[] resp)
    {
        if (resp.Length < 12) return null;
        int qd = (resp[4] << 8) | resp[5];
        int an = (resp[6] << 8) | resp[7];
        if (an == 0) return null;
        int pos = 12;
        // skip question section
        for (int i = 0; i < qd; i++)
        {
            while (pos < resp.Length && resp[pos] != 0) pos += resp[pos] + 1;
            pos += 5; // null + type + class
        }
        // answers
        for (int i = 0; i < an; i++)
        {
            // name may be pointer or literal
            if ((resp[pos] & 0xC0) == 0xC0) pos += 2; else { while (pos < resp.Length && resp[pos] != 0) pos += resp[pos] + 1; pos++; }
            if (pos + 10 > resp.Length) return null;
            int type = (resp[pos] << 8) | resp[pos + 1];
            int rdlen = (resp[pos + 8] << 8) | resp[pos + 9];
            pos += 10;
            if (type == 1 && rdlen == 4) return new IPAddress(resp.AsSpan(pos, 4).ToArray());
            pos += rdlen;
        }
        return null;
    }

    public static async Task<DnsTestResult> TestAsync(DnsServer server, string probeHost = "www.google.com", CancellationToken ct = default)
    {
        if (server.Kind == "system")
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var addrs = await Dns.GetHostAddressesAsync(probeHost, ct).ConfigureAwait(false);
                sw.Stop();
                var v4 = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                return v4 is null ? new DnsTestResult(false, (int)sw.ElapsedMilliseconds, null, "no A record")
                                  : new DnsTestResult(true, (int)sw.ElapsedMilliseconds, v4.ToString(), null);
            }
            catch (Exception ex) { return new DnsTestResult(false, -1, null, ex.Message); }
        }

        if (server.Kind == "doh" && !string.IsNullOrEmpty(server.DohUrl))
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(6) };
                var url = $"{server.DohUrl}?name={Uri.EscapeDataString(probeHost)}&type=A";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("accept", "application/dns-json");
                var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                sw.Stop();
                var ip = ExtractFirstAnswerIp(json);
                return ip is null ? new DnsTestResult(false, (int)sw.ElapsedMilliseconds, null, "no answer")
                                  : new DnsTestResult(true, (int)sw.ElapsedMilliseconds, ip, null);
            }
            catch (Exception ex) { return new DnsTestResult(false, -1, null, ex.Message); }
        }

        // Default: plain UDP toward Primary
        var samples = new List<int>();
        IPAddress? last = null;
        for (var i = 0; i < 3; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (ip, ms) = UdpQuery(server.Primary, probeHost, 2500);
            if (ip is not null) { samples.Add(ms); last = ip; }
            await Task.Delay(80, ct).ConfigureAwait(false);
        }
        if (samples.Count == 0) return new DnsTestResult(false, -1, null, "no response");
        samples.Sort();
        return new DnsTestResult(true, samples[samples.Count / 2], last?.ToString(), null);
    }

    private static string? ExtractFirstAnswerIp(string dnsJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(dnsJson);
            foreach (var a in doc.RootElement.EnumerateArray())
            {
                if (!a.TryGetProperty("type", out var t) || t.GetInt32() != 1) continue;
                if (a.TryGetProperty("data", out var data))
                    return data.GetString();
            }
        }
        catch { }
        return null;
    }

    /// <summary>Bypass-check: resolve gemini.google.com through this resolver and through system.
    /// Different result ⇒ the resolver relays sanctioned domains.</summary>
    public static async Task<bool> RelaysSanctionedDomains(DnsServer server, CancellationToken ct = default)
    {
        const string geminiHost = "gemini.google.com";
        try
        {
            var viaCandidate = await TestAsync(server, geminiHost, ct).ConfigureAwait(false);
            if (!viaCandidate.Success || string.IsNullOrEmpty(viaCandidate.ResolvedIp)) return false;

            var sysAddrs = await Dns.GetHostAddressesAsync(geminiHost, ct).ConfigureAwait(false);
            var sysIp = sysAddrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString();
            if (string.IsNullOrEmpty(sysIp)) return true; // system can't even reach it → candidate wins

            return !string.Equals(viaCandidate.ResolvedIp, sysIp, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
