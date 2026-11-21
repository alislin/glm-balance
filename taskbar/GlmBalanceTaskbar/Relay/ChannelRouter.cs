namespace GlmBalanceTaskbar;

/// <summary>
/// Provider 池路由：选路 + 失败转移候选。
/// 选路规则：sticky pin 强制通道候选优先（池内 ∧ 首跳 ∧ 支持模型 ∧ 可路由，命中即选），
/// 未命中完全回退既有规则：支持该模型 ∧ Enabled ∧ 有 key ∧ 状态可路由 → Priority 升序取
/// 第一个（"默认优先，用量还有就优先"）；平序按配置数组顺序。锁保护线程安全。
/// pin 为运行时单槽意图（不入配置、重启失效；删除渠道双保险清除）。
/// </summary>
public sealed class ChannelRouter
{
    readonly object _gate = new();
    List<ProviderRuntime> _pool = new();
    string? _forcedId; // sticky pin 强制通道 id（单槽，后 pin 替换前 pin；null = 纯自动）

    /// <summary>
    /// 原子替换池（配置变更后重建；进行中的请求持旧引用不受影响）。
    /// 防御性校验（R4 双保险之二）：pin 的渠道已从池中消失（被删除）→ 清空，不留悬空意图；
    /// 正常路径由 AdminApi DELETE 显式清除。
    /// </summary>
    public void ReplacePool(List<ProviderRuntime> pool)
    {
        lock (_gate)
        {
            _pool = pool;
            if (_forcedId != null && !pool.Exists(rt => rt.Id == _forcedId))
            {
                _forcedId = null;
            }
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

    /// <summary>sticky pin 强制通道 id（null = 无 pin）。运行时意图：不写配置、重启即失效。</summary>
    public string? ForcedId
    {
        get { lock (_gate) { return _forcedId; } }
    }

    /// <summary>
    /// 设为强制通道（单槽，后 pin 替换前 pin）。仅当 id 存在于当前池时置位并返回 true；
    /// false = 渠道不在池（含 rt 查出后池被重建的极小竞态，调用方按 404 语义处理）。
    /// </summary>
    public bool SetForced(string id)
    {
        lock (_gate)
        {
            if (_pool.Exists(rt => rt.Id == id))
            {
                _forcedId = id;
                return true;
            }
            return false;
        }
    }

    /// <summary>解除强制（幂等）。仅当 pin 恰为该 id 时清除——pin 指向其他 id 时不清除
    /// （解除必须显式针对被 pin 渠道）。</summary>
    public void ClearForced(string? id)
    {
        lock (_gate)
        {
            if (id != null && _forcedId == id)
            {
                _forcedId = null;
            }
        }
    }

    /// <summary>
    /// 为 model 选一个可路由渠道；exclude 为本轮已失败的渠道（失败转移用）。
    /// pin 候选先判（n1 顺序声明）：池内 ∧ 不在 exclude（首跳）∧ Supports ∧ IsRoutable →
    /// 直接选 pin；未命中完全回退既有 Priority 规则，请求不失败（pin 只影响首选，
    /// 不改 failover 链语义）。返回 null = 无可用渠道。
    /// </summary>
    public ProviderRuntime? Select(string model, IReadOnlySet<string> exclude, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_forcedId is { } forced)
            {
                var pinned = _pool.FirstOrDefault(rt =>
                    rt.Id == forced
                    && !exclude.Contains(rt.Id)
                    && rt.Supports(model)
                    && rt.IsRoutable(now));
                if (pinned != null) return pinned;
            }
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

    /// <summary>
    /// "当前通道"：全模型维度第一个可路由渠道（UI 与 /status 展示）。
    /// pin 候选优先（不带模型维度）：池内 ∧ IsRoutable 即返回；否则既有 Priority 规则。
    /// </summary>
    public ProviderRuntime? Current(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_forcedId is { } forced)
            {
                var pinned = _pool.FirstOrDefault(rt => rt.Id == forced && rt.IsRoutable(now));
                if (pinned != null) return pinned;
            }
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