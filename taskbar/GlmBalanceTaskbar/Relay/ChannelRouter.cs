namespace GlmBalanceTaskbar;

/// <summary>
/// Provider 池路由：选路 + 失败转移候选。
/// 选路规则：支持该模型 ∧ Enabled ∧ 有 key ∧ 状态可路由 → Priority 升序取第一个
/// （"默认优先，用量还有就优先"）；平序按配置数组顺序。锁保护线程安全。
/// </summary>
public sealed class ChannelRouter
{
    readonly object _gate = new();
    List<ProviderRuntime> _pool = new();

    /// <summary>原子替换池（配置变更后重建；进行中的请求持旧引用不受影响）。</summary>
    public void ReplacePool(List<ProviderRuntime> pool)
    {
        lock (_gate)
        {
            _pool = pool;
        }
    }

    /// <summary>当前池快照（配置顺序）。</summary>
    public List<ProviderRuntime> Pool()
    {
        lock (_gate)
        {
            return _pool.ToList();
        }
    }

    /// <summary>
    /// 为 model 选一个可路由渠道；exclude 为本轮已失败的渠道（失败转移用）。
    /// 返回 null = 无可用渠道。
    /// </summary>
    public ProviderRuntime? Select(string model, IReadOnlySet<string> exclude, DateTimeOffset now)
    {
        lock (_gate)
        {
            ProviderRuntime? best = null;
            foreach (var rt in _pool)
            {
                if (exclude.Contains(rt.Id)) continue;
                if (!rt.Supports(model)) continue;
                if (!rt.IsRoutable(now)) continue;
                if (best == null
                    || rt.Config.Priority < best.Config.Priority)
                {
                    best = rt;
                }
            }
            return best;
        }
    }

    /// <summary>"当前通道"：全模型维度第一个可路由渠道（UI 与 /status 展示）。</summary>
    public ProviderRuntime? Current(DateTimeOffset now)
    {
        lock (_gate)
        {
            ProviderRuntime? best = null;
            foreach (var rt in _pool)
            {
                if (!rt.IsRoutable(now)) continue;
                if (best == null || rt.Config.Priority < best.Config.Priority)
                {
                    best = rt;
                }
            }
            return best;
        }
    }

    /// <summary>池内 Enabled 渠道的 models 并集（保持首次出现顺序）。</summary>
    public List<string> ModelUnion()
    {
        lock (_gate)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var models = new List<string>();
            foreach (var rt in _pool)
            {
                if (!rt.Config.Enabled) continue;
                foreach (var m in rt.Config.Models)
                {
                    if (seen.Add(m)) models.Add(m);
                }
            }
            return models;
        }
    }
}
