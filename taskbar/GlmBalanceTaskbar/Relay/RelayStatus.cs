namespace GlmBalanceTaskbar;

using System.Text.Json.Serialization;

/// <summary>渠道状态快照（WinForms / /status / admin 共用；不含 key）。</summary>
public sealed record ProviderStatus(
    string Id,
    string Name,
    int Priority,
    string Probe,
    bool Enabled,
    bool HasKey,
    string State,
    string? DegradedReason,
    string? CooldownReason,
    int? CooldownRemainingSec,
    int ErrorStreak,
    int? RemainingPct,
    int? FiveHourPct,
    int? WeekPct,
    string? SourceLabel,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? LastProbeAt,
    string? LastError,
    long RequestCount,
    long FailCount,
    double AvgLatencyMs);

/// <summary>强制通道（sticky pin）快照：id + 当前是否可路由（false = 让位自动规则中）+ 不可用短因。</summary>
public sealed record ForcedRouteStatus(string Id, bool Active, string? Reason);

/// <summary>中转服务整体状态快照（Initialized：配置存在 relay 段；false = 未初始化引导态）。
/// Dev：--dev 实例标记（N1 裁决：属性级 WhenWritingDefault 忽略——生产（false）序列化省略字段、
/// dev（true）携带；不改全局 RelayJson.Options，生产 /status 响应形状逐字节不变）。
/// Forced：sticky pin 强制通道（同 Dev 字段模式：null → 序列化省略，无 pin 时生产 /status
/// 响应逐字节不变；有 pin 时为增量可选字段，既有消费者不感知）。</summary>
public sealed record RelayStatus(
    bool Running,
    bool Enabled,
    bool Initialized,
    int Port,
    string? CurrentId,
    DateTimeOffset? StartedAt,
    double UptimeSec,
    long TodayRequests,
    long TodayFailovers,
    bool AuthDisabled,
    bool SeededDefaults,
    IReadOnlyList<ProviderStatus> Providers,
    IReadOnlyList<string> Models,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Dev = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ForcedRouteStatus? Forced = null);
