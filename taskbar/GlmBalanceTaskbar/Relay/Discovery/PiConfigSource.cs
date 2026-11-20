namespace GlmBalanceTaskbar;

/// <summary>
/// Pi 配置发现：占位实现。configPath 可配（relay.agents.pi.configPath），当前扫描返回空结果。
/// </summary>
public sealed class PiConfigSource : IAgentConfigSource
{
    readonly string _configPath;

    public PiConfigSource(string configPath) => _configPath = configPath;

    public string AgentId => "pi";

    public Task<IReadOnlyList<DiscoveredProvider>> DiscoverAsync(CancellationToken ct = default)
    {
        // 占位：Pi 配置格式未定稿；configPath 未配置或文件不存在时返回空结果。
        IReadOnlyList<DiscoveredProvider> empty = Array.Empty<DiscoveredProvider>();
        return Task.FromResult(empty);
    }
}
