namespace GlmBalanceTaskbar;

/// <summary>Pi 部署器：占位实现（接口齐备，待 Pi 配置格式定稿后补齐）。</summary>
public sealed class PiDeployer : IAgentDeployer
{
    public string AgentId => "pi";

    public Task<DeployPreview> PreviewAsync(DeployRequest req, CancellationToken ct = default)
    {
        return Task.FromResult(new DeployPreview(
            AgentId, "(未定稿)",
            "Pi 部署未实现（占位）：Pi 配置格式尚未定稿，本版本仅预留接口与 configPath 配置项。",
            Ready: false));
    }

    public Task<DeployResult> DeployAsync(DeployRequest req, CancellationToken ct = default)
    {
        return Task.FromResult(new DeployResult(
            false, null, "Pi 部署未实现（占位）：Pi 配置格式尚未定稿，未做任何变更。"));
    }
}
