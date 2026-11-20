using System.Text.Json;

namespace GlmBalanceTaskbar;

/// <summary>
/// opencode 配置发现：枚举 auth.json + opencode.json 的全部 provider 条目（合并去重）。
/// baseURL 含 bigmodel.cn / api.z.ai → SuggestedProbeType = zhipu-monitor，否则 none。
/// </summary>
public sealed class OpenCodeConfigSource : IAgentConfigSource
{
    public string AgentId => "opencode";

    public Task<IReadOnlyList<DiscoveredProvider>> DiscoverAsync(CancellationToken ct = default)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var byId = new Dictionary<string, DiscoveredProvider>(StringComparer.Ordinal);

        // 1) auth.json：条目名即 provider id，key 在 entry.key
        var authPath = Path.Combine(home, ".local", "share", "opencode", "auth.json");
        foreach (var (id, key) in ReadAuthEntries(authPath))
        {
            var baseUrl = KnownBaseUrls.TryGetValue(id, out var b) ? b : null;
            byId[id] = new DiscoveredProvider(
                AgentId, id, DisplayName(id), baseUrl, key,
                Models: null,
                SuggestedProbeType: SuggestProbe(id, baseUrl),
                Source: "auth.json");
        }

        // 2) opencode.json：provider.<id> { name / options.baseURL / options.apiKey / models{} }
        var ocPath = Path.Combine(home, ".config", "opencode", "opencode.json");
        foreach (var p in ReadOpencodeProviders(ocPath))
        {
            if (byId.TryGetValue(p.Id, out var prev))
            {
                // 合并：opencode.json 条目信息更全（baseURL/models），key 取先到者
                var key = p.ApiKey ?? prev.ApiKey;
                var models = p.Models ?? prev.Models;
                byId[p.Id] = p with { ApiKey = key, Models = models, Source = "opencode.json + auth.json" };
            }
            else
            {
                byId[p.Id] = p;
            }
        }

        IReadOnlyList<DiscoveredProvider> list = byId.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        return Task.FromResult(list);
    }

    internal static IReadOnlyList<(string Id, string? Key)> ReadAuthEntries(string path)
    {
        var result = new List<(string, string?)>();
        try
        {
            if (!File.Exists(path)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var e in doc.RootElement.EnumerateObject())
            {
                result.Add((e.Name, e.Value.Prop("key").Str()));
            }
        }
        catch
        {
            /* 读失败 → 空 */
        }
        return result;
    }

    internal static IReadOnlyList<DiscoveredProvider> ReadOpencodeProviders(string path)
    {
        var result = new List<DiscoveredProvider>();
        try
        {
            if (!File.Exists(path)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var providers = doc.RootElement.Prop("provider");
            if (providers is not { ValueKind: JsonValueKind.Object }) return result;
            foreach (var p in providers.Value.EnumerateObject())
            {
                var v = p.Value;
                var name = v.Prop("name").Str() ?? p.Name;
                var baseUrl = v.Prop("options").Prop("baseURL").Str()
                    ?? v.Prop("options").Prop("baseUrl").Str();
                var key = v.Prop("options").Prop("apiKey").Str() ?? v.Prop("key").Str();
                IReadOnlyList<string>? models = null;
                if (v.Prop("models") is { ValueKind: JsonValueKind.Object } ms)
                {
                    models = ms.EnumerateObject().Select(m => m.Name).ToList();
                }
                result.Add(new DiscoveredProvider(
                    "opencode", p.Name, name, baseUrl, key, models,
                    SuggestProbe(p.Name, baseUrl), "opencode.json"));
            }
        }
        catch
        {
            /* 读失败 → 空 */
        }
        return result;
    }

    static string SuggestProbe(string id, string? baseUrl)
    {
        var haystack = $"{id} {baseUrl}";
        return haystack.Contains("bigmodel.cn", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("api.z.ai", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("zhipu", StringComparison.OrdinalIgnoreCase)
            ? "zhipu-monitor"
            : "none";
    }

    static string DisplayName(string id) => id switch
    {
        "zhipuai-coding-plan" => "智谱 Coding Plan",
        "minimax-cn" => "MiniMax CN",
        _ => id,
    };

    static readonly Dictionary<string, string> KnownBaseUrls = new(StringComparer.Ordinal)
    {
        ["zhipuai-coding-plan"] = "https://open.bigmodel.cn/api/coding/paas/v4",
    };
}
