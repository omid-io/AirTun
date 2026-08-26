using System.IO;
using System.Text.Json;
using AirTun.Core.Resolvers;

namespace AirTun.Core.Resolvers;

/// <summary>
/// Bridges the DNS tab selection to the tunnel: resolves the active DnsServer
/// to a DoH URL (or null = system/UDP default) that airtun-tun should use.
/// Reads the same dns.json the UI writes — no extra state.
/// </summary>
public static class DnsSelectionStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AirTun", "dns.json");

    /// <summary>Returns the active resolver's DoH URL, or null when system/UDP selected.</summary>
    public static string? GetActiveDohUrl()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var activeId = doc.RootElement.GetProperty("activeId").GetString();
            if (string.IsNullOrEmpty(activeId) || !activeId.StartsWith("builtin-"))
            {
                // custom server: find it in customServers
                if (doc.RootElement.TryGetProperty("customServers", out var customs))
                {
                    foreach (var s in customs.EnumerateArray())
                    {
                        if (s.TryGetProperty("id", out var id) && id.GetString() == activeId)
                        {
                            var kind = s.TryGetProperty("kind", out var k) ? k.GetString() : "udp";
                            if (kind == "doh" && s.TryGetProperty("dohUrl", out var u))
                                return u.GetString();
                            return null; // custom UDP → tunnel keeps its default UDP forwarder
                        }
                    }
                }
                return null;
            }
            // builtin: look up its DoH URL from the preset list
            return DnsStore.BuiltIns().FirstOrDefault(s => s.Id == activeId)?.DohUrl;
        }
        catch { return null; }
    }
}
