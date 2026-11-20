namespace GlmBalanceTaskbar;

/// <summary>"不可检查"空实现：声明纯反应式（仅靠 429/402/403 与连续错误兜底）。</summary>
public sealed class NoProbe : IUsageProbe
{
    public string Type => "none";

    public Task<ProbeResult> ProbeAsync(ProviderConfig p, CancellationToken ct) =>
        Task.FromResult(new ProbeResult(Available: true, RemainingPct: null, ResetAt: null, Error: null));
}
