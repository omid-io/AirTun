using System.Net;
using System.Text.Json;

namespace AirTun.Core.Geo;

public sealed record GeoIpInfo(
    string Ip,
    string Country,
    string CountryCode,
    string City,
    string Isp,
    string FlagEmoji
);

public sealed class GeoIpService
{
    private readonly HttpClient _httpClient;

    public GeoIpService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    public async Task<GeoIpInfo?> FetchOutboundGeoAsync(string? proxyHost = null, int? proxyPort = null, CancellationToken ct = default)
    {
        SocketsHttpHandler? handler = null;
        HttpClient? customClient = null;
        try
        {
            if (!string.IsNullOrEmpty(proxyHost) && proxyPort.HasValue && proxyPort.Value > 0)
            {
                handler = new SocketsHttpHandler
                {
                    Proxy = new WebProxy($"socks5://{proxyHost}:{proxyPort.Value}"),
                    UseProxy = true,
                    ConnectTimeout = TimeSpan.FromSeconds(6),
                };
                customClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            }
            var client = customClient ?? _httpClient;

            var url = "http://ip-api.com/json/?fields=status,country,countryCode,city,isp,query";
            var response = await client.GetStringAsync(url, ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var status) && status.GetString() == "success")
            {
                var ip = root.GetProperty("query").GetString() ?? "Unknown IP";
                var country = root.GetProperty("country").GetString() ?? "Unknown";
                var countryCode = root.GetProperty("countryCode").GetString() ?? "UN";
                var city = root.GetProperty("city").GetString() ?? "";
                var isp = root.GetProperty("isp").GetString() ?? "";
                var flag = CountryCodeToEmoji(countryCode);

                return new GeoIpInfo(ip, country, countryCode, city, isp, flag);
            }
        }
        catch
        {
            try
            {
                var client = customClient ?? _httpClient;
                var ip = await client.GetStringAsync("https://api.ipify.org", ct).ConfigureAwait(false);
                if (IPAddress.TryParse(ip.Trim(), out _))
                {
                    return new GeoIpInfo(ip.Trim(), "Connected", "OK", "", "Encrypted Tunnel", "🌐");
                }
            }
            catch { }
        }
        finally
        {
            customClient?.Dispose();
            handler?.Dispose();
        }

        return null;
    }

    public static string CountryCodeToEmoji(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Length != 2)
            return "🌐";

        var code = countryCode.ToUpperInvariant();
        var first = char.ConvertToUtf32(code, 0) + 0x1F1A5;
        var second = char.ConvertToUtf32(code, 1) + 0x1F1A5;

        return char.ConvertFromUtf32(first) + char.ConvertFromUtf32(second);
    }
}
