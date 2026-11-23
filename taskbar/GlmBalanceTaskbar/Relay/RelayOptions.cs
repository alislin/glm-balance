using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GlmBalanceTaskbar;

/// <summary>relay 渠道（provider 池条目）配置。</summary>
public sealed class ProviderConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Priority { get; set; } = 100;
    /// <summary>探测类型：zhipu-monitor / none。</summary>
    public string Probe { get; set; } = "none";
    public string BaseUrl { get; set; } = "";
    /// <summary>空 → 复用全局 apiKey 读取链（auth.json 优先 zhipuai-coding-plan）。</summary>
    public string ApiKey { get; set; } = "";
    public List<string> Models { get; set; } = new();
    public bool Enabled { get; set; } = true;

    // ── zhipu-monitor 探测要素（provider 级覆盖；null = 回落全局链）。混合账号池：每渠道绑定自己的账号维度。 ──
    /// <summary>团队组织 ID（bigmodel-organization 头）；null/空 = 回落全局。</summary>
    public string? Organization { get; set; }
    /// <summary>团队项目 ID（bigmodel-project 头）；null/空 = 回落全局。</summary>
    public string? Project { get; set; }
    /// <summary>用量维度：1=个人 2=团队；null = 自动（有 org 推导 2）。</summary>
    public int? UsageType { get; set; }
    /// <summary>监控 API 域名；null/空 = 由 baseURL 域名推导，再回落全局。</summary>
    public string? MonitorBase { get; set; }

    /// <summary>是否有任一探测要素为 provider 自有（决定探测能否复用全局快照：有覆盖 → 不复用，防止跨账号冒用）。</summary>
    public bool HasProbeOverrides =>
        !string.IsNullOrWhiteSpace(Organization) ||
        !string.IsNullOrWhiteSpace(Project) ||
        UsageType != null ||
        !string.IsNullOrWhiteSpace(MonitorBase);

    /// <summary>配置写回节点（真实 key，仅供 glmbalance-taskbar.json 唯一写路径使用）。</summary>
    public JsonObject ToJsonNode()
    {
        var node = new JsonObject
        {
            ["id"] = Id,
            ["name"] = Name,
            ["priority"] = Priority,
            ["probe"] = Probe,
            ["baseURL"] = BaseUrl,
            ["apiKey"] = ApiKey,
            ["models"] = new JsonArray(Models.Select(m => JsonValue.Create(m)).ToArray()),
            ["enabled"] = Enabled,
        };
        // 探测要素：null 不写键（缺省 = 回落全局，配置文件保持干净）
        if (!string.IsNullOrWhiteSpace(Organization)) node["organization"] = Organization;
        if (!string.IsNullOrWhiteSpace(Project)) node["project"] = Project;
        if (UsageType is int ut) node["usageType"] = ut;
        if (!string.IsNullOrWhiteSpace(MonitorBase)) node["monitorBase"] = MonitorBase;
        return node;
    }
}

/// <summary>硬冷却参数（响应驱动）。</summary>
public sealed class RelayCooldownOptions
{
    /// <summary>上游 429/402/403 的冷却时长（秒）。</summary>
    public int QuotaSeconds { get; set; } = 300;
    /// <summary>5xx/网络异常连续失败进入冷却的次数阈值。</summary>
    public int ErrorThreshold { get; set; } = 3;
    /// <summary>连续错误触发的冷却时长（秒）。</summary>
    public int ErrorSeconds { get; set; } = 60;
}

/// <summary>SSE 流式转发超时参数（响应驱动）。</summary>
public sealed class RelayStreamOptions
{
    /// <summary>
    /// 上游响应头已到但首个 chunk 未到的空闲上限（秒）。大 prompt 的首块延迟属正常请求
    /// （上游在处理，只是慢），宁可多等不可误杀；超时视为渠道级故障，向客户端回结构化错误。
    /// </summary>
    public int FirstChunkSeconds { get; set; } = 600;
    /// <summary>开流后（已收到并写出 chunk）的上游 idle 上限（秒）。此时已无法转移，向客户端发 SSE 错误帧后干净收尾。</summary>
    public int IdleSeconds { get; set; } = 300;
}

/// <summary>
/// relay 配置段（glmbalance-taskbar.json 的 "relay" 节）。
/// "relay" 段整体缺省 → 中转关闭，原功能零影响。
/// </summary>
public sealed class RelayOptions
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 8787;
    /// <summary>空 → 首启生成随机 token（32 字节 hex）写回配置文件。</summary>
    public string LocalToken { get; set; } = "";
    public int ProbeIntervalSeconds { get; set; } = 60;
    public int DegradeBelowPct { get; set; } = 5;
    public int RestoreAbovePct { get; set; } = 10;
    public RelayCooldownOptions Cooldown { get; set; } = new();
    public RelayStreamOptions Stream { get; set; } = new();
    public List<ProviderConfig> Providers { get; set; } = new();
    /// <summary>agents.pi.configPath（Pi 配置路径，占位预留）。</summary>
    public string PiConfigPath { get; set; } = "";
    /// <summary>解析时因 providers 缺失/为空而播种了默认渠道（需写回）。</summary>
    public bool Seeded { get; set; }

    /// <summary>relay.port 是否来自显式配置（false = 默认值 8787）。
    /// dev 合成用它区分「未配置 → 默认改 8788」与「显式写 8787 → 保留，由 Start() 硬禁拦截」。</summary>
    public bool PortExplicit { get; set; }

    public string BaseUrl => $"http://127.0.0.1:{Port}/v1";
    public string AdminUrl => $"http://127.0.0.1:{Port}/admin";

    /// <summary>
    /// 未初始化态（配置无 relay 段）的内存默认：参数取默认值并播种渠道，Enabled=true；
    /// 仅在用户点击「启用中转」时经 Start() 的 token/播种持久化路径写回，不在启动时自动落盘。
    /// </summary>
    public static RelayOptions CreateDefault()
    {
        var o = new RelayOptions { Enabled = true };
        o.SeedDefaults();
        return o;
    }

    /// <summary>解析 "relay" 段；段缺失返回 null（中转关闭）；解析失败记 warnings 并返回 null。</summary>
    public static RelayOptions? Parse(JsonElement root, List<string> warnings)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var r = root.Prop("relay");
        if (r is not { ValueKind: JsonValueKind.Object } relay)
        {
            if (r != null) warnings.Add("relay 段格式异常（应为对象），已忽略：中转关闭");
            return null;
        }

        var o = new RelayOptions { Enabled = true };
        try
        {
            o.Enabled = relay.Prop("enabled").BoolVal() ?? true;
            o.Port = (int?)relay.Prop("port").Num() ?? 8787;
            o.PortExplicit = relay.Prop("port").Num() != null;
            o.LocalToken = relay.Prop("localToken").Str() ?? "";
            o.ProbeIntervalSeconds = (int?)relay.Prop("probeIntervalSeconds").Num() ?? 60;
            o.DegradeBelowPct = (int?)relay.Prop("degradeBelowPct").Num() ?? 5;
            o.RestoreAbovePct = (int?)relay.Prop("restoreAbovePct").Num() ?? 10;

            if (relay.Prop("cooldown") is { ValueKind: JsonValueKind.Object } cd)
            {
                o.Cooldown.QuotaSeconds = (int?)cd.Prop("quotaSeconds").Num() ?? 300;
                o.Cooldown.ErrorThreshold = (int?)cd.Prop("errorThreshold").Num() ?? 3;
                o.Cooldown.ErrorSeconds = (int?)cd.Prop("errorSeconds").Num() ?? 60;
            }

            if (relay.Prop("stream") is { ValueKind: JsonValueKind.Object } st)
            {
                o.Stream.FirstChunkSeconds = (int?)st.Prop("firstChunkSeconds").Num() ?? 600;
                o.Stream.IdleSeconds = (int?)st.Prop("idleSeconds").Num() ?? 300;
            }

            if (relay.Prop("providers") is { ValueKind: JsonValueKind.Array } ps)
            {
                foreach (var p in ps.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object) continue;
                    var id = p.Prop("id").Str() ?? "";
                    if (id.Length == 0)
                    {
                        warnings.Add("relay.providers 存在缺少 id 的条目，已忽略");
                        continue;
                    }
                    var pc = new ProviderConfig
                    {
                        Id = id,
                        Name = p.Prop("name").Str() ?? id,
                        Priority = (int?)p.Prop("priority").Num() ?? 100,
                        Probe = p.Prop("probe").Str() ?? "none",
                        BaseUrl = p.Prop("baseURL").Str() ?? p.Prop("baseUrl").Str() ?? "",
                        ApiKey = p.Prop("apiKey").Str() ?? "",
                        Enabled = p.Prop("enabled").BoolVal() ?? true,
                        Organization = p.Prop("organization").Str(),
                        Project = p.Prop("project").Str(),
                        UsageType = (int?)p.Prop("usageType").Num(),
                        MonitorBase = p.Prop("monitorBase").Str(),
                    };
                    if (p.Prop("models") is { ValueKind: JsonValueKind.Array } ms)
                    {
                        pc.Models = ms.EnumerateArray()
                            .Select(m => m.Str() ?? "")
                            .Where(s => s.Length > 0)
                            .ToList();
                    }
                    o.Providers.Add(pc);
                }
            }

            if (relay.Prop("agents").Prop("pi").Prop("configPath").Str() is { Length: > 0 } piPath)
            {
                o.PiConfigPath = piPath;
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"relay 段解析失败：{ex.Message}（中转关闭）");
            return null;
        }

        if (o.Providers.Count == 0)
        {
            o.SeedDefaults();
            o.Seeded = true;
        }

        // 钳制到合理范围
        o.Port = Math.Clamp(o.Port, 1, 65535);
        o.ProbeIntervalSeconds = Math.Clamp(o.ProbeIntervalSeconds, 10, 3600);
        o.DegradeBelowPct = Math.Clamp(o.DegradeBelowPct, 0, 100);
        o.RestoreAbovePct = Math.Clamp(o.RestoreAbovePct, 0, 100);
        o.Cooldown.QuotaSeconds = Math.Clamp(o.Cooldown.QuotaSeconds, 5, 86400);
        o.Cooldown.ErrorThreshold = Math.Clamp(o.Cooldown.ErrorThreshold, 1, 100);
        o.Cooldown.ErrorSeconds = Math.Clamp(o.Cooldown.ErrorSeconds, 5, 86400);
        o.Stream.FirstChunkSeconds = Math.Clamp(o.Stream.FirstChunkSeconds, 10, 1800);
        o.Stream.IdleSeconds = Math.Clamp(o.Stream.IdleSeconds, 10, 1800);
        return o;
    }

    /// <summary>播种默认渠道（幂等：仅当前池为空时）。与契约默认一致：zhipu-coding 优先、custom-relay 兜底。</summary>
    public void SeedDefaults()
    {
        if (Providers.Count > 0) return;
        Providers.Add(new ProviderConfig
        {
            Id = "zhipu-coding",
            Name = "智谱 Coding",
            Priority = 1,
            Probe = "zhipu-monitor",
            BaseUrl = "https://open.bigmodel.cn/api/coding/paas/v4",
            ApiKey = "",
            Models = new List<string> { "glm-5.3-flash", "glm-5.3" },
            Enabled = true,
        });
        Providers.Add(new ProviderConfig
        {
            Id = "custom-relay",
            Name = "自定义中转",
            Priority = 2,
            Probe = "none",
            BaseUrl = "https://your-relay.example.com/v1",
            ApiKey = "",
            Models = new List<string> { "glm-5.3-flash", "glm-5.3" },
            Enabled = true,
        });
        Seeded = true;
    }

    /// <summary>整个 relay 段序列化为 JsonNode（全应用唯一写路径的载荷）。</summary>
    public JsonObject ToJson()
    {
        var arr = new JsonArray();
        foreach (var p in Providers)
        {
            arr.Add(p.ToJsonNode());
        }
        return new JsonObject
        {
            ["enabled"] = Enabled,
            ["port"] = Port,
            ["localToken"] = LocalToken,
            ["probeIntervalSeconds"] = ProbeIntervalSeconds,
            ["degradeBelowPct"] = DegradeBelowPct,
            ["restoreAbovePct"] = RestoreAbovePct,
            ["cooldown"] = new JsonObject
            {
                ["quotaSeconds"] = Cooldown.QuotaSeconds,
                ["errorThreshold"] = Cooldown.ErrorThreshold,
                ["errorSeconds"] = Cooldown.ErrorSeconds,
            },
            ["stream"] = new JsonObject
            {
                ["firstChunkSeconds"] = Stream.FirstChunkSeconds,
                ["idleSeconds"] = Stream.IdleSeconds,
            },
            ["providers"] = arr,
            ["agents"] = new JsonObject
            {
                ["pi"] = new JsonObject { ["configPath"] = PiConfigPath },
            },
        };
    }

    /// <summary>
    /// 合并写回：读取原文件 → mutate(relay 节) → 原样保留其余字段写回。
    /// 全应用唯一的 glmbalance-taskbar.json 写路径（持久化锁由调用方持有）。
    /// </summary>
    public static bool Persist(string path, Func<JsonObject, JsonObject> mutateRelay, List<string>? warnings = null)
    {
        try
        {
            JsonObject root;
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                using var doc = JsonDocument.Parse(stream);
                root = JsonNode.Parse(doc.RootElement.GetRawText())!.AsObject();
            }
            else
            {
                root = new JsonObject();
            }
            var relayNode = EnsureRelay(root);
            var mutated = mutateRelay(relayNode);
            if (mutated != null && !ReferenceEquals(mutated, relayNode))
            {
                // 整节点替换式 mutator（如 _opts.ToJson() 重序列化）：挂回根节点。
                // 就地修改式 mutator 返回同一引用，此处无操作。修复：返回值此前被丢弃，
                // 导致 ToJson() 式写回静默失效（relay 节点写成 {}）。
                root["relay"] = mutated;
            }
            WriteAtomic(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex)
        {
            warnings?.Add($"配置写回失败（{Path.GetFileName(path)}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>原子写：临时文件 + File.Replace（目标不存在则直接移动），避免写一半崩溃留半截 JSON。</summary>
    static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    /// <summary>在根对象上取或建 relay 节。</summary>
    public static JsonObject EnsureRelay(JsonObject root)
    {
        if (root["relay"] is JsonObject o) return o;
        var relay = new JsonObject();
        root["relay"] = relay;
        return relay;
    }
}

/// <summary>API 响应序列化选项（camelCase）。</summary>
public static class RelayJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>配置文件写回的缩进选项。</summary>
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static string Quote(string s) => JsonSerializer.Serialize(s);


    /// <summary>apiKey 掩码：sk-…后4位；空 key 返回空串。</summary>
    public static string MaskKey(string key) =>
        string.IsNullOrEmpty(key) ? "" : key.Length <= 4 ? "sk-…" : $"sk-…{key[^4..]}";
}
