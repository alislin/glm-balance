using System.Globalization;

namespace GlmBalanceTaskbar;

/// <summary>一次探测的结果。</summary>
/// <param name="Available">探测是否可用（失败 = false，不改渠道状态仅记错误）。</param>
/// <param name="RemainingPct">剩余百分比（不可检查 = null，纯反应式）。</param>
/// <param name="ResetAt">窗口重置时间（未知 = null）。</param>
/// <param name="Error">探测失败原因。</param>
/// <param name="SourceLabel">剩余量的来源窗口标签（如 "5h"/"周积分"，仅展示用）。</param>
/// <param name="FiveHourPct">5 小时窗口剩余百分比（未知 = null）。</param>
/// <param name="WeekPct">周窗口剩余百分比（未知 = null）。</param>
/// <param name="Snap">完整用量快照（zhipu-monitor 专用：供详情窗/托盘复用，探测是唯一数据面）。</param>
/// <param name="Activity">活跃度（zhipu-monitor 专用：探测循环按渠道缓存，5min TTL）。</param>
public sealed record ProbeResult(
    bool Available,
    int? RemainingPct,
    DateTimeOffset? ResetAt,
    string? Error,
    string? SourceLabel = null,
    int? FiveHourPct = null,
    int? WeekPct = null,
    UsageSnapshot? Snap = null,
    ActivityInfo? Activity = null);

/// <summary>用量探测接口。</summary>
public interface IUsageProbe
{
    string Type { get; }
    Task<ProbeResult> ProbeAsync(ProviderConfig p, CancellationToken ct);
}

/// <summary>探测共享上下文：快照缓存提供者 + 新鲜窗口（秒）+ 全局配置。</summary>
public sealed class ProbeContext
{
    /// <summary>取 AppContext 喂入的最新快照（可能为 null）。</summary>
    public required Func<UsageSnapshot?> SnapshotProvider { get; init; }
    /// <summary>快照新鲜窗口（秒，动态读取，= probeIntervalSeconds + 10）。</summary>
    public required Func<int> FreshSeconds { get; init; }
    /// <summary>全局配置（apiKey 读取链 / 组织头 / 默认 MonitorBase）。</summary>
    public required AppConfig Global { get; init; }
    /// <summary>
    /// 活跃度按渠道缓存（key=provider id）：探测实例每次 Resolve 都新建，缓存必须挂共享上下文；
    /// 值 = (活跃度, 抓取时刻)，ZhipuMonitorProbe 以 5min TTL 复用，避免每轮探测重复请求。
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, (ActivityInfo Info, DateTimeOffset FetchedAt)> ActivityCache { get; }
        = new();
}

/// <summary>探测类型注册表。</summary>
public static class ProbeRegistry
{
    public static IUsageProbe Resolve(string type, ProbeContext ctx) => (type ?? "").Trim().ToLowerInvariant() switch
    {
        "zhipu-monitor" => new ZhipuMonitorProbe(ctx),
        _ => new NoProbe(), // none / 未知类型一律视为不可检查（纯反应式）
    };
}
