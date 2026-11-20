namespace GlmBalanceTaskbar;

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
    string? SourceLabel,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? LastProbeAt,
    string? LastError,
    long RequestCount,
    long FailCount,
    double AvgLatencyMs);

/// <summary>中转服务整体状态快照。</summary>
public sealed record RelayStatus(
    bool Running,
    bool Enabled,
    int Port,
    string? CurrentId,
    DateTimeOffset? StartedAt,
    double UptimeSec,
    long TodayRequests,
    long TodayFailovers,
    bool AuthDisabled,
    bool SeededDefaults,
    IReadOnlyList<ProviderStatus> Providers,
    IReadOnlyList<string> Models);
