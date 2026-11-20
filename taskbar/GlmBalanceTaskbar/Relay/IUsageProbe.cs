using System.Globalization;

namespace GlmBalanceTaskbar;

/// <summary>一次探测的结果。</summary>
/// <param name="Available">探测是否可用（失败 = false，不改渠道状态仅记错误）。</param>
/// <param name="RemainingPct">剩余百分比（不可检查 = null，纯反应式）。</param>
/// <param name="ResetAt">窗口重置时间（未知 = null）。</param>
/// <param name="Error">探测失败原因。</param>
/// <param name="SourceLabel">剩余量的来源窗口标签（如 "5h"/"周积分"，仅展示用）。</param>
public sealed record ProbeResult(
    bool Available,
    int? RemainingPct,
    DateTimeOffset? ResetAt,
    string? Error,
    string? SourceLabel = null);

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
