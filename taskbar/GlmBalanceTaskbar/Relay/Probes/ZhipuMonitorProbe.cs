namespace GlmBalanceTaskbar;

/// <summary>
/// 智谱监控探测：quota/limit（复用 GlmApiClient）。
/// 数据源优化：优先复用 RelayManager 喂入的 AppContext 最新 UsageSnapshot
/// （距抓取时刻 &lt; probeIntervalSeconds + 10s 视为新鲜，直接取窗口剩余），
/// 过期才自行请求。窗口语义与 QuotaParser 对齐（unit=3 → 5h、unit=6 → 周）。
/// </summary>
public sealed class ZhipuMonitorProbe : IUsageProbe
{
    readonly ProbeContext _ctx;

    public ZhipuMonitorProbe(ProbeContext ctx) => _ctx = ctx;

    public string Type => "zhipu-monitor";

    public async Task<ProbeResult> ProbeAsync(ProviderConfig p, CancellationToken ct)
    {
        // 1) 复用 AppContext 快照（30s 配额轮询的最新结果）
        var snap = _ctx.SnapshotProvider();
        if (snap is { Error: null } && DateTimeOffset.Now - snap.FetchedAt < TimeSpan.FromSeconds(_ctx.FreshSeconds()))
        {
            return Pick(snap);
        }

        // 2) 过期/缺失 → 自行请求
        var key = NonEmpty(p.ApiKey) ?? _ctx.Global.ApiKey;
        if (key == null)
        {
            return new ProbeResult(false, null, null, "无 API key（provider 与全局读取链均为空）");
        }

        var probeCfg = new AppConfig
        {
            ApiKey = key,
            MonitorBase = MonitorBase(p),
            Organization = _ctx.Global.Organization,
            Project = _ctx.Global.Project,
            UsageType = _ctx.Global.UsageType,
            Activity = false,
        };
        using var client = new GlmApiClient(probeCfg);
        var fresh = await client.FetchUsageAsync(ct).ConfigureAwait(false);
        if (fresh.Error != null)
        {
            return new ProbeResult(false, null, null, fresh.Error);
        }
        return Pick(fresh);
    }

    /// <summary>取 5h / 周窗口剩余的较小者（先耗尽的窗口决定可用性），并标注来源；
    /// 两窗口剩余分别给出（admin 总览分条展示）。</summary>
    static ProbeResult Pick(UsageSnapshot s)
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
            WeekPct: s.Week?.Remaining);
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
