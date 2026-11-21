namespace GlmBalanceTaskbar;

/// <summary>
/// IAgentDeployer 装饰器（dev 专用）：PreviewAsync 透传（本就只读），DeployAsync →
/// 仅调 PreviewAsync 并合成成功结果，绝不触碰真实部署器的写盘路径（WriteAtomicAsync）。
/// G5：开发实例下 opencode/pi 部署只预览、绝不落盘。
/// </summary>
public sealed class DryRunDeployer : IAgentDeployer
{
    readonly IAgentDeployer _inner;

    public DryRunDeployer(IAgentDeployer inner) => _inner = inner;

    public string AgentId => _inner.AgentId;

    public Task<DeployPreview> PreviewAsync(DeployRequest req, CancellationToken ct = default) =>
        _inner.PreviewAsync(req, ct);

    public async Task<DeployResult> DeployAsync(DeployRequest req, CancellationToken ct = default)
    {
        var preview = await _inner.PreviewAsync(req, ct).ConfigureAwait(false);
        if (!preview.Ready)
        {
            return new DeployResult(false, null, $"[dev] dry-run：预览失败，未落盘。\n{preview.Summary}");
        }
        return new DeployResult(true, null,
            "[dev] dry-run：仅预览未落盘\n" + preview.Summary);
    }
}
