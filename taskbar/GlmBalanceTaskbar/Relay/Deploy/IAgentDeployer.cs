namespace GlmBalanceTaskbar;

/// <summary>部署请求：把本中转注册为某智能体的 provider。</summary>
public sealed record DeployRequest(
    string BaseUrl,
    string Token,
    IReadOnlyList<string> Models,
    bool SetDefault);

/// <summary>部署预览（dryRun 结果；不落盘不备份）。</summary>
public sealed record DeployPreview(
    string Agent,
    string ConfigPath,
    string Summary,
    bool Ready);

/// <summary>部署执行结果。</summary>
public sealed record DeployResult(bool Success, string? BackupPath, string Message);

/// <summary>智能体部署器接口。</summary>
public interface IAgentDeployer
{
    string AgentId { get; }
    Task<DeployPreview> PreviewAsync(DeployRequest req, CancellationToken ct = default);
    Task<DeployResult> DeployAsync(DeployRequest req, CancellationToken ct = default);
}
