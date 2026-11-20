namespace GlmBalanceTaskbar;

/// <summary>从智能体配置中发现的一个 provider 条目（key 仅内存使用与掩码展示，不外泄明文）。</summary>
public sealed record DiscoveredProvider(
    string Agent,
    string Id,
    string Name,
    string? BaseUrl,
    string? ApiKey,
    IReadOnlyList<string>? Models,
    string SuggestedProbeType,
    string Source);

/// <summary>智能体配置源：枚举某智能体配置中的全部 provider 条目。</summary>
public interface IAgentConfigSource
{
    string AgentId { get; }
    Task<IReadOnlyList<DiscoveredProvider>> DiscoverAsync(CancellationToken ct = default);
}
