namespace GlmBalanceTaskbar;

/// <summary>
/// 智谱监控探测：quota/limit + 活跃度（复用 GlmApiClient）。
/// 探测是唯一数据面：ProbeResult 携带完整 UsageSnapshot / ActivityInfo，
/// 详情窗 Tab、托盘浮窗、标题栏全部复用探测产物，不做独立请求（除非手动刷新）。
/// 窗口语义与 QuotaParser 对齐（unit=3 → 5h、unit=6 → 周）。
/// </summary>
public sealed class ZhipuMonitorProbe : IUsageProbe
{
    readonly ProbeContext _ctx;

    public ZhipuMonitorProbe(ProbeContext ctx) => _ctx = ctx;

    public string Type => "zhipu-monitor";

    /// <summary>活跃度 TTL（毫秒），与插件 ACTIVITY_TTL 一致。</summary>
    public const int ActivityTtlMs = 300_000;

    public async Task<ProbeResult> ProbeAsync(ProviderConfig p, CancellationToken ct)
    {
        // --once/dev 注入快照（AppContext 主面板不再喂快照；仅评估模式用）
        var injected = _ctx.SnapshotProvider();
        if (injected is { Error: null } && DateTimeOffset.Now - injected.FetchedAt < TimeSpan.FromSeconds(_ctx.FreshSeconds()))
        {
            return Pick(injected);
        }

        // 要素逐项 provider 优先，null 回落全局；与 key 的 ?? 模式一致
        var key = NonEmpty(p.ApiKey) ?? _ctx.Global.ApiKey;
        if (key == null)
        {
            return new ProbeResult(false, null, null, "无 API key（provider 与全局读取链均为空）");
        }

        var probeCfg = new AppConfig
        {
            ApiKey = key,
            MonitorBase = MonitorBase(p), // 监控域名与渠道 baseURL 绑定（域名推导），无 provider 级覆盖
            Organization = NonEmpty(p.Organization) ?? _ctx.Global.Organization,
            Project = NonEmpty(p.Project) ?? _ctx.Global.Project,
            // usageType：provider 显式（1/2）优先；null = 自动，按解析后的组织归属推导（有 org → 2）
            UsageType = p.UsageType ?? ((NonEmpty(p.Organization) ?? _ctx.Global.Organization) != null ? 2 : null),
            Activity = true, // 活跃度在探测内完成（TTL 内复用缓存，不重复请求）
        };
        using var client = new GlmApiClient(probeCfg);
        var fresh = await client.FetchUsageAsync(ct).ConfigureAwait(false);
        if (fresh.Error != null)
        {
            return new ProbeResult(false, null, null, fresh.Error);
        }

        // 活跃度：TTL 内复用缓存（零请求）；过期才抓；失败保留旧值（手动刷新经 force=true 强制重抓）
        var activity = await GetActivityAsync(p, client, ct, force: ct == CancellationToken.None).ConfigureAwait(false);
        fresh.Activity = activity;
        return Pick(fresh);
    }

    /// <summary>活跃度按渠道获取：缓存新鲜 → 直接返回；过期/缺失 → 抓取并落缓存；失败 → 旧值兜底（无旧值 = null）。
    /// 缓存挂在 ProbeContext（Resolve 每轮新建探测实例，实例字段无法跨轮命中）。手动刷新跳过缓存强制重抓。</summary>
    async Task<ActivityInfo?> GetActivityAsync(ProviderConfig p, GlmApiClient client, CancellationToken ct, bool force = false)
    {
        var now = DateTimeOffset.Now;
        if (!force && _ctx.ActivityCache.TryGetValue(p.Id, out var c) && now - c.FetchedAt < TimeSpan.FromMilliseconds(ActivityTtlMs))
        {
            return c.Info;
        }
        var act = await client.FetchActivityAsync(ct).ConfigureAwait(false);
        if (act != null)
        {
            _ctx.ActivityCache[p.Id] = (act, now);
            return act;
        }
        // 抓取失败：返回过期旧值兜底（下轮探测重试）
        return _ctx.ActivityCache.TryGetValue(p.Id, out var old) ? old.Info : null;
    }

    /// <summary>渠道配置变更（要素重绑定/跨账号）后失效活跃度缓存，避免串数据。</summary>
    public static void InvalidateActivity(ProbeContext ctx, string providerId) => ctx.ActivityCache.TryRemove(providerId, out _);

    /// <summary>取 5h / 周窗口剩余的较小者（先耗尽的窗口决定可用性），并标注来源；
    /// 完整快照随 ProbeResult 交付（详情窗/托盘复用）。</summary>
    ProbeResult Pick(UsageSnapshot s)
    {
        int? best = null;
        string? label = null;
        if (s.FiveHour?.Remaining is int fh)
        {
            best = fh;
            label = s.FiveHour.Label ?? "5h";
        }
        if (s.Week?.Remaining is int wk && (best == null || wk < best))
        {
            best = wk;
            label = s.Week.Label ?? "周积分";
        }
        return new ProbeResult(true, best, null, null, label,
            FiveHourPct: s.FiveHour?.Remaining,
            WeekPct: s.Week?.Remaining,
            Snap: s);
    }

    string MonitorBase(ProviderConfig p)
    {
        if (NonEmpty(p.BaseUrl) is { } url &&
            Uri.TryCreate(url, UriKind.Absolute, out var u) &&
            (u.Host.Contains("bigmodel.cn", StringComparison.OrdinalIgnoreCase) ||
             u.Host.Contains("z.ai", StringComparison.OrdinalIgnoreCase)))
        {
            return $"{u.Scheme}://{u.Host}";
        }
        return _ctx.Global.MonitorBase;
    }

    static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
