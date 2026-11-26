using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlmBalanceTaskbar;

/// <summary>
/// key 链 home 重定向单一静态点（§2.3 方案 A）：headless 以 <c>--config-dir &lt;profile&gt;</c> 注入
/// 用户 profile 根（SYSTEM 账户下仍能解析 auth.json / opencode 配置链，且跟随 key 轮换不冻结）。
/// null（未注入）= 逐字节现状（Environment.GetFolderPath(UserProfile)）。仅影响
/// auth.json / opencode.json / tui.json 读取链与发现/部署的 home 三处（Config / OpenCodeConfigSource /
/// OpenCodeDeployer）；配置文件本体（glmbalance-taskbar.json）位置不受影响，恒为 exe 同目录。
/// </summary>
public static class HomeOverride
{
    static string? _path;

    /// <summary>注入重定向（headless 启动早期调用一次；null/空白 = 清除，回到现状）。</summary>
    public static void Set(string? path) =>
        _path = string.IsNullOrWhiteSpace(path) ? null : path;

    /// <summary>key 链 home：重定向值优先，否则现状 UserProfile（null 时与原实现逐字节一致，G6）。</summary>
    public static string GetUserProfile() =>
        _path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

public sealed class AppConfig
{
    public string? ApiKey { get; set; }
    public string? Organization { get; set; }
    public string? Project { get; set; }
    /// <summary>用量维度：2=团队 1=个人；默认有 organization 时为 2，否则不带</summary>
    public int? UsageType { get; set; }
    public string MonitorBase { get; set; } = "https://open.bigmodel.cn";
    public int IntervalSeconds { get; set; } = 30;
    public bool Activity { get; set; } = true;
    public int ActivityType { get; set; } = 3;
    /// <summary>中转配置段；null = "relay" 缺省或解析失败（中转关闭，原功能零影响）。</summary>
    public RelayOptions? Relay { get; set; }

    /// <summary>devMock 配置段（仅 --dev 语义使用；生产 Load 不读该键，JSON 未知键自动忽略，生产零影响）。</summary>
    public DevMockOptions? DevMock { get; set; }

    public List<string> Warnings { get; } = new();

    /// <summary>
    /// 配置来源（优先级从高到低）：
    /// 1. <paramref name="configDir"/>（null = 现状：exe 同目录；--dev 传 DevMode.ConfigRoot）下的 glmbalance-taskbar.json
    /// 2. ~/.local/share/opencode/auth.json（API key，同 e2e-check.ts）
    /// 3. ~/.config/opencode/opencode.json(.jsonc)（provider apiKey / baseURL，jsonc 优先见 OpenCodeConfig）
    /// 4. ~/.config/opencode/tui.json（glm-balance 插件条目的 organization / project 等选项，同 deploy.mjs 匹配规则）
    /// </summary>
    public static AppConfig Load(string? configDir = null)
    {
        var cfg = new AppConfig();
        var home = HomeOverride.GetUserProfile(); // §2.3：经单一重定向点（null = 现状逐字节一致）

        string? ovBase = null, tuiBase = null, providerBase = null, providerKey = null;
        string? tuiOrg = null, tuiProject = null;
        int? tuiUsageType = null, tuiActivityType = null;
        bool? tuiActivity = null;
        bool ovActivity = false, ovActivityType = false, ovBaseSet = false;

        // 1) 本地覆盖：configDir 指定目录（null = 现状 exe 同目录；dev = DevMode.ConfigRoot）下 glmbalance-taskbar.json
        var overridePath = Path.Combine(
            configDir ??
            Path.GetDirectoryName(Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0]) ??
            ".",
            "glmbalance-taskbar.json");
        using (var ov = TryReadJson(overridePath, cfg.Warnings))
        {
            if (ov != null)
            {
                var r = ov.RootElement;
                cfg.ApiKey = NonEmpty(r.Prop("apiKey").Str());
                cfg.Organization = NonEmpty(r.Prop("organization").Str());
                cfg.Project = NonEmpty(r.Prop("project").Str());
                cfg.UsageType = (int?)r.Prop("usageType").Num();
                cfg.IntervalSeconds = (int?)r.Prop("intervalSeconds").Num() ?? 30;
                if (r.Prop("activity").BoolVal() is { } a) { cfg.Activity = a; ovActivity = true; }
                if (r.Prop("activityType").Num() is { } at) { cfg.ActivityType = (int)at; ovActivityType = true; }
                ovBase = NonEmpty(r.Prop("monitorBase").Str());
                ovBaseSet = ovBase != null;

                // relay 段：整体缺省 → null（中转关闭）；解析失败 → warnings + 中转关闭
                cfg.Relay = RelayOptions.Parse(r, cfg.Warnings);

                // devMock 段（m4：默认值以 DevMockOptions 类默认为单一来源，解析只覆盖显式项）
                if (r.Prop("devMock") is { ValueKind: JsonValueKind.Object } dm)
                {
                    var mo = new DevMockOptions();
                    if ((int?)dm.Prop("upstreamPort").Num() is { } up) mo.UpstreamPort = up;
                    if (dm.Prop("chatContent").Str() is { } cc) mo.ChatContent = cc;
                    if ((int?)dm.Prop("latencyMs").Num() is { } lm) mo.LatencyMs = lm;
                    if ((int?)dm.Prop("quota").Prop("fiveHourRemainingPct").Num() is { } q5) mo.FiveHourRemainingPct = Math.Clamp(q5, 0, 100);
                    if ((int?)dm.Prop("quota").Prop("weekRemainingPct").Num() is { } qw) mo.WeekRemainingPct = Math.Clamp(qw, 0, 100);
                    if (dm.Prop("providers") is { ValueKind: JsonValueKind.Object } ps)
                    {
                        foreach (var e in ps.EnumerateObject())
                        {
                            var v = e.Value;
                            mo.Providers[e.Name] = new DevMockProviderRules
                            {
                                ChatStatus = (int?)v.Prop("chatStatus").Num() ?? 200,
                                ModelsStatus = (int?)v.Prop("modelsStatus").Num() ?? 200,
                                StreamBreak = v.Prop("streamBreak").BoolVal() ?? false,
                                StreamStallSeconds = (int?)v.Prop("streamStallSeconds").Num() ?? 0,
                                StreamStallAfterChunks = (int?)v.Prop("streamStallAfterChunks").Num() ?? 0,
                            };
                        }
                    }
                    cfg.DevMock = mo;
                }
            }
        }

        // 2) auth.json：遍历属性名含 zhipu/bigmodel/z.ai 的条目，优先 zhipuai-coding-plan
        var authPath = Path.Combine(home, ".local", "share", "opencode", "auth.json");
        string? authKey = null;
        using (var doc = TryReadJson(authPath, cfg.Warnings))
        {
            if (doc?.RootElement.ValueKind == JsonValueKind.Object)
            {
                var bestScore = 0;
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (!ZhipuIdRe.IsMatch(p.Name)) continue;
                    var key = p.Value.Prop("key").Str();
                    if (key == null) continue;
                    var score = p.Name == "zhipuai-coding-plan" ? 2 : 1;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        authKey = key;
                    }
                }
            }
        }

        // 3) opencode.json(.jsonc)：provider 的 baseURL / apiKey
        var ocPath = OpenCodeConfig.ResolveConfigPath(home); // jsonc 优先（opencode 合并语义 jsonc 覆盖 json）
        using (var doc = TryReadJson(ocPath, cfg.Warnings))
        {
            var providers = doc?.RootElement.Prop("provider");
            if (providers?.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in providers.Value.EnumerateObject())
                {
                    var v = p.Value;
                    var bu = v.Prop("options").Prop("baseURL").Str();
                    var idName = $"{p.Name} {v.Prop("id").Str() ?? ""} {v.Prop("name").Str() ?? ""}";
                    if ((bu != null && GlmHostRe.IsMatch(bu)) || ZhipuIdRe.IsMatch(idName))
                    {
                        providerBase = bu;
                        providerKey = v.Prop("options").Prop("apiKey").Str() ?? v.Prop("key").Str();
                        break;
                    }
                }
            }
        }

        // 4) tui.json：plugin 数组中 glm-balance 条目的选项元组
        var tuiPath = Path.Combine(home, ".config", "opencode", "tui.json");
        using (var doc = TryReadJson(tuiPath, cfg.Warnings))
        {
            var plugins = doc?.RootElement.Prop("plugin");
            if (plugins?.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in plugins.Value.EnumerateArray())
                {
                    string? path = entry.ValueKind == JsonValueKind.String ? entry.GetString()
                        : entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() >= 2 ? entry[0].Str()
                        : null;
                    if (path == null || !path.Replace('\\', '/').ToLowerInvariant().Contains("glm-balance")) continue;

                    var opt = entry.ValueKind == JsonValueKind.Array ? entry[1] : (JsonElement?)null;
                    if (opt is not { ValueKind: JsonValueKind.Object }) break;
                    var o = opt.Value;
                    tuiOrg = NonEmpty(o.Prop("organization").Str());
                    tuiProject = NonEmpty(o.Prop("project").Str());
                    tuiUsageType = (int?)o.Prop("usageType").Num();
                    tuiBase = NonEmpty(o.Prop("monitorBase").Str());
                    tuiActivity = o.Prop("activity").BoolVal();
                    tuiActivityType = (int?)o.Prop("activityType").Num();
                    break;
                }
            }
        }

        // 合成（override > auth.json > opencode.json；org/project/monitorBase：override > tui.json > 推导）
        cfg.ApiKey ??= authKey ?? providerKey;
        cfg.Organization ??= tuiOrg;
        cfg.Project ??= tuiProject;
        cfg.UsageType ??= tuiUsageType ?? (cfg.Organization != null ? 2 : null);
        if (ovBaseSet) cfg.MonitorBase = ovBase!;
        else if (tuiBase != null) cfg.MonitorBase = tuiBase;
        else if (providerBase != null) cfg.MonitorBase = MonitorBaseFrom(providerBase);
        if (!ovActivity && tuiActivity is { } ta) cfg.Activity = ta;
        if (!ovActivityType && tuiActivityType is { } tat) cfg.ActivityType = tat;

        cfg.IntervalSeconds = Math.Clamp(cfg.IntervalSeconds, 5, 3600);
        if (cfg.ApiKey == null)
        {
            cfg.Warnings.Add(
                "未找到 API key：请确认 ~/.local/share/opencode/auth.json 或 ~/.config/opencode/opencode.json(.jsonc) 已配置智谱 provider，" +
                "或在 exe 同目录 glmbalance-taskbar.json 中设置 apiKey");
        }
        return cfg;
    }

    static readonly Regex ZhipuIdRe = new(@"zhipu|bigmodel|z\.ai", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex GlmHostRe = new(@"open\.bigmodel\.cn|dev\.bigmodel\.cn|bigmodel\.cn|api\.z\.ai", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string MonitorBaseFrom(string baseURL)
    {
        if (Uri.TryCreate(baseURL, UriKind.Absolute, out var u)) return $"{u.Scheme}://{u.Host}";
        var i = baseURL.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? baseURL[..i] : baseURL;
    }

    static JsonDocument? TryReadJson(string path, List<string> warnings)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return JsonDocument.Parse(stream, OpenCodeConfig.Parsing); // JSONC 容错（opencode.jsonc 合法含注释/尾逗号；对纯 JSON 零影响）
        }
        catch (Exception ex)
        {
            warnings.Add($"读取 {path} 失败：{ex.Message}");
            return null;
        }
    }

    static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
