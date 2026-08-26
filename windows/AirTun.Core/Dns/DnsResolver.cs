using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirTun.Core.Resolvers;

/// <summary>One selectable DNS resolver entry (built-in preset or user-defined).</summary>
public sealed class DnsServer
{
    [JsonPropertyName("id")] public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    /// <summary>udp | doh | system</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "udp";
    [JsonPropertyName("primary")] public string Primary { get; set; } = "";
    [JsonPropertyName("secondary")] public string? Secondary { get; set; }
    /// <summary>DoH endpoint URL (kind=doh only)</summary>
    [JsonPropertyName("dohUrl")] public string? DohUrl { get; set; }
    /// <summary>Built-ins cannot be deleted/renamed by the user.</summary>
    [JsonPropertyName("builtIn")] public bool BuiltIn { get; set; }

    public override string ToString() => $"{Label} ({Primary})";
}

/// <summary>Persists the DNS server list + active selection to AppData.</summary>
public static class DnsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirTun");
    private static readonly string FilePath = Path.Combine(Dir, "dns.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static List<DnsServer> BuiltIns() => new()
    {
        new() { Id="builtin-system",   Label="System (default)", Kind="system", Primary="", BuiltIn=true },
        new() { Id="builtin-cloudflare", Label="Cloudflare", Kind="udp", Primary="1.1.1.1", Secondary="1.0.0.1",
               DohUrl="https://1.1.1.1/dns-query", BuiltIn=true },
        new() { Id="builtin-google",   Label="Google",  Kind="udp", Primary="8.8.8.8", Secondary="8.8.4.4",
               DohUrl="https://dns.google/dns-query", BuiltIn=true },
        new() { Id="builtin-quad9",    Label="Quad9",   Kind="udp", Primary="9.9.9.9", Secondary="149.112.112.112",
               DohUrl="https://dns.quad9.net/dns-query", BuiltIn=true },
        // Iranian anti-sanction resolvers (bypass sanctioned domains via relay)
        new() { Id="builtin-403",      Label="403.online", Kind="udp", Primary="10.202.10.202", Secondary="10.202.10.102",
               DohUrl="https://dns.403.online/dns-query", BuiltIn=true },
        new() { Id="builtin-electro",  Label="Electro", Kind="udp", Primary="78.157.42.100", Secondary="78.157.42.101", BuiltIn=true },
        new() { Id="builtin-radar",    Label="Radar Game", Kind="udp", Primary="10.202.10.10", Secondary="10.202.10.11", BuiltIn=true },
        new() { Id="builtin-shecan",   Label="Shecan (free)", Kind="udp", Primary="178.22.122.100", Secondary="185.51.200.2",
               DohUrl="https://free.shecan.ir/dns-query", BuiltIn=true },
        new() { Id="builtin-begzar",   Label="Begzar", Kind="udp", Primary="185.55.226.26", Secondary="185.55.225.25", BuiltIn=true },
    };

    public static (List<DnsServer> Servers, string ActiveId) Load()
    {
        var servers = BuiltIns();
        var activeId = "builtin-system";
        try
        {
            if (File.Exists(FilePath))
            {
                var doc = JsonSerializer.Deserialize<DnsPersisted>(File.ReadAllText(FilePath), JsonOpts);
                if (doc is not null)
                {
                    activeId = string.IsNullOrEmpty(doc.ActiveId) ? activeId : doc.ActiveId;
                    if (doc.CustomServers is { Count: > 0 }) servers.AddRange(doc.CustomServers);
                    else
                    {
                        // merge user selection of a builtin even without customs
                        if (doc.BuiltinActiveIndex is int idx && idx >= 0 && idx < servers.Count)
                            activeId = servers[idx].Id;
                    }
                }
            }
        }
        catch { /* corrupt file -> defaults */ }
        return (servers, activeId);
    }

    public static void Save(List<DnsServer> allServers, string activeId)
    {
        Directory.CreateDirectory(Dir);
        var custom = allServers.FindAll(s => !s.BuiltIn);
        var doc = new DnsPersisted { ActiveId = activeId, CustomServers = custom };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(doc, JsonOpts));
    }

    private sealed class DnsPersisted
    {
        [JsonPropertyName("activeId")] public string ActiveId { get; set; } = "";
        [JsonPropertyName("customServers")] public List<DnsServer>? CustomServers { get; set; }
        [JsonPropertyName("builtinActiveIndex")] public int? BuiltinActiveIndex { get; set; }
    }
}
