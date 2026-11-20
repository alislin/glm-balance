namespace GlmBalanceTaskbar;

public enum ProviderState
{
    /// <summary>正常，参与路由。</summary>
    Normal,
    /// <summary>硬冷却（响应驱动：429/402/403 或连续错误）。</summary>
    Cooldown,
    /// <summary>软降级（探测驱动：用量低于阈值 / 无 key）。</summary>
    Degraded,
    /// <summary>无 key，不参与路由。</summary>
    DisabledNoKey,
}

public static class ProviderStateText
{
    public static string Of(ProviderState s) => s switch
    {
        ProviderState.Normal => "normal",
        ProviderState.Cooldown => "cooldown",
        ProviderState.Degraded => "degraded",
        ProviderState.DisabledNoKey => "no-key",
        _ => "?",
    };
}

/// <summary>
/// 渠道运行时：三态状态机 + 计数 + lastError/lastUsed + 统计。
/// 冷却用时间戳实现（无定时器），到期由读取方惰性归位；全部可变状态在锁内。
/// </summary>
public sealed class ProviderRuntime
{
    readonly object _gate = new();
    readonly RelayOptions _opts;
    readonly Func<ProviderConfig, string?>? _keyRefresher;

    ProviderState _state = ProviderState.Normal;
    DateTimeOffset? _cooldownUntil;
    string? _cooldownReason;
    int _errorStreak;
    string? _degradedReason;
    int? _remainingPct;
    int? _fiveHourPct;
    int? _weekPct;
    string? _sourceLabel;
    DateTimeOffset? _resetAt;
    DateTimeOffset? _lastProbeAt;
    string? _lastError;
    DateTimeOffset? _lastUsedAt;
    long _requestCount;
    long _failCount;
    long _totalLatencyMs;

    public ProviderRuntime(ProviderConfig config, RelayOptions opts, Func<ProviderConfig, string?>? keyRefresher = null)
    {
        Config = config;
        _opts = opts;
        _keyRefresher = keyRefresher;
    }

    public ProviderConfig Config { get; }
    /// <summary>解析后的实际 key（provider 显式 key 优先，空则全局读取链）；null = 无 key。</summary>
    public string? ResolvedKey { get; private set; }

    public string Id => Config.Id;

    /// <summary>构建时解析实际 key；null 表示无 key（不参与路由）。</summary>
    public void SetResolvedKey(string? key)
    {
        ResolvedKey = string.IsNullOrWhiteSpace(key) ? null : key;
        if (ResolvedKey == null)
        {
            lock (_gate)
            {
                _state = ProviderState.DisabledNoKey;
                _degradedReason = "无 API key";
                _lastError = "apiKey 为空且不满足全局读取链回退条件（第三方渠道需显式配置 key）";
            }
        }
    }

    /// <summary>
    /// 重解析 key（鉴权类冷却归位时调用，F2）：重读配置与全局读取链，
    /// 避免归位后持失效旧 key 形成"冷却→401"循环直至重启。
    /// </summary>
    public void RefreshKey()
    {
        if (_keyRefresher == null) return;
        var key = _keyRefresher(Config);
        SetResolvedKey(key);
    }

    /// <summary>冷却到期归位（锁内调用）：归位 + 重解析 key + 清计数。</summary>
    void CooldownExpire_Locked()
    {
        _state = ProviderState.Normal;
        _cooldownUntil = null;
        _cooldownReason = null;
        _errorStreak = 0;
    }

    public bool Supports(string model) => Config.Models.Contains(model);

    /// <summary>当前是否可被路由（Enabled ∧ 有 key ∧ 状态 normal；冷却到期惰性归位并重解析 key；degraded/no-key 不参与路由）。</summary>
    public bool IsRoutable(DateTimeOffset now)
    {
        var expired = false;
        bool routable;
        lock (_gate)
        {
            if (!Config.Enabled || _state == ProviderState.DisabledNoKey) return false;
            if (_state == ProviderState.Cooldown && _cooldownUntil is { } until && now >= until)
            {
                CooldownExpire_Locked();
                expired = true;
            }
            routable = _state == ProviderState.Normal;
        }
        if (expired) RefreshKey(); // 锁外执行：鉴权类冷却归位重读 key
        return routable;
    }

    /// <summary>请求成功：错误计数清零。</summary>
    public void NoteSuccess(long latencyMs)
    {
        lock (_gate)
        {
            _errorStreak = 0;
            _requestCount++;
            _totalLatencyMs += latencyMs;
            _lastUsedAt = DateTimeOffset.Now;
            _lastError = null;
            if (_state == ProviderState.Cooldown && (_cooldownUntil == null || DateTimeOffset.Now >= _cooldownUntil))
            {
                _state = ProviderState.Normal;
                _cooldownUntil = null;
                _cooldownReason = null;
            }
        }
    }

    /// <summary>上游 429/402/403：硬冷却 quotaSeconds。</summary>
    public void NoteQuotaFailure(string error, DateTimeOffset now)
    {
        lock (_gate)
        {
            _failCount++;
            _requestCount++;
            _errorStreak = 0;
            _state = ProviderState.Cooldown;
            _cooldownUntil = now.AddSeconds(_opts.Cooldown.QuotaSeconds);
            _cooldownReason = "配额类错误";
            _lastError = error;
            _lastUsedAt = now;
        }
    }

    /// <summary>上游 5xx / 网络异常：计数，连续 ≥ errorThreshold 次进入冷却 errorSeconds。</summary>
    public void NoteErrorFailure(string error, DateTimeOffset now)
    {
        lock (_gate)
        {
            _failCount++;
            _requestCount++;
            _errorStreak++;
            _lastError = error;
            _lastUsedAt = now;
            if (_errorStreak >= _opts.Cooldown.ErrorThreshold)
            {
                _state = ProviderState.Cooldown;
                _cooldownUntil = now.AddSeconds(_opts.Cooldown.ErrorSeconds);
                _cooldownReason = $"连续 {_errorStreak} 次错误";
            }
        }
    }

    /// <summary>上游 4xx 业务错：不转移不冷却，仅记录。</summary>
    public void NoteClientError(int statusCode, string error, DateTimeOffset now)
    {
        lock (_gate)
        {
            _requestCount++;
            _lastError = error;
            _lastUsedAt = now;
        }
    }

    /// <summary>SSE 中途断流：不重试，该渠道错误计数 +1。</summary>
    public void NoteStreamBroken(string error, DateTimeOffset now)
    {
        lock (_gate)
        {
            _errorStreak++;
            _lastError = error;
            if (_errorStreak >= _opts.Cooldown.ErrorThreshold)
            {
                _state = ProviderState.Cooldown;
                _cooldownUntil = now.AddSeconds(_opts.Cooldown.ErrorSeconds);
                _cooldownReason = $"连续 {_errorStreak} 次错误";
            }
        }
    }

    /// <summary>探测结果落地（探测失败/不可用不改状态，仅记错误）。</summary>
    public void ApplyProbe(ProbeResult r, DateTimeOffset now)
    {
        lock (_gate)
        {
            _lastProbeAt = now;
            if (!r.Available)
            {
                // 探测失败不改状态：反应式信号始终兜底
                _lastError = $"探测失败：{r.Error ?? "未知"}";
                return;
            }
            _remainingPct = r.RemainingPct;
            _fiveHourPct = r.FiveHourPct;
            _weekPct = r.WeekPct;
            _sourceLabel = r.SourceLabel;
            _resetAt = r.ResetAt;

            if (_state == ProviderState.DisabledNoKey) return;
            if (_state == ProviderState.Cooldown && _cooldownUntil is { } until && now < until) return;

            if (r.RemainingPct is int pct)
            {
                if (pct < _opts.DegradeBelowPct)
                {
                    _degradedReason = $"{r.SourceLabel ?? "用量"} 剩余 {pct}%";
                    _state = ProviderState.Degraded;
                    return;
                }
                if (pct >= _opts.RestoreAbovePct && _state == ProviderState.Degraded)
                {
                    _state = ProviderState.Normal;
                    _degradedReason = null;
                    return;
                }
            }
            // 窗口重置后恢复（ResetAt 已过视为窗口翻新）
            if (_state == ProviderState.Degraded && r.ResetAt is { } reset && now >= reset)
            {
                _state = ProviderState.Normal;
                _degradedReason = null;
            }
        }
    }

    /// <summary>管理操作（配置变更/导入）后强制归位（no-key 除外）。</summary>
    public void ForceReevaluate()
    {
        lock (_gate)
        {
            if (_state == ProviderState.DisabledNoKey) return;
            _state = _state == ProviderState.Degraded ? ProviderState.Degraded : ProviderState.Normal;
            _cooldownUntil = null;
            _cooldownReason = null;
            _errorStreak = 0;
        }
    }

    /// <summary>冷却剩余秒（未在冷却返回 null）。</summary>
    public int? CooldownRemaining(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_state != ProviderState.Cooldown || _cooldownUntil is not { } until) return null;
            var sec = (int)Math.Ceiling((until - now).TotalSeconds);
            return Math.Max(0, sec);
        }
    }

    /// <summary>状态检视快照（UI / status / admin / --once 共读）。</summary>
    public sealed record Inspection(
        ProviderState State,
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

    public Inspection Inspect(DateTimeOffset now)
    {
        var expired = false;
        Inspection ins;
        lock (_gate)
        {
            var state = _state;
            var remain = (int?)null;
            if (state == ProviderState.Cooldown && _cooldownUntil is { } until)
            {
                if (now >= until)
                {
                    CooldownExpire_Locked();
                    expired = true;
                    state = ProviderState.Normal;
                }
                else
                {
                    remain = (int)Math.Ceiling((until - now).TotalSeconds);
                }
            }
            ins = new Inspection(state, _degradedReason, _cooldownReason, remain, _errorStreak,
                _remainingPct, _fiveHourPct, _weekPct, _sourceLabel, _lastUsedAt, _lastProbeAt, _lastError,
                _requestCount, _failCount,
                _requestCount > 0 ? (double)_totalLatencyMs / _requestCount : 0);
        }
        if (expired) RefreshKey(); // 锁外执行
        return ins;
    }

    /// <summary>同 id 重建池时继承旧运行时的状态与统计（配置热更新不丢观测）。</summary>
    public void Adopt(ProviderRuntime prev)
    {
        var snap = prev.Inspect(DateTimeOffset.Now);
        lock (_gate)
        {
            if (_state == ProviderState.DisabledNoKey) return;
            _state = snap.State == ProviderState.DisabledNoKey ? ProviderState.Normal : snap.State;
            _cooldownReason = snap.CooldownReason;
            _errorStreak = snap.ErrorStreak;
            _degradedReason = snap.DegradedReason;
            _remainingPct = snap.RemainingPct;
            _fiveHourPct = snap.FiveHourPct;
            _weekPct = snap.WeekPct;
            _sourceLabel = snap.SourceLabel;
            _lastProbeAt = snap.LastProbeAt;
            _lastError = snap.LastError;
            _lastUsedAt = snap.LastUsedAt;
            _requestCount = snap.RequestCount;
            _failCount = snap.FailCount;
            _totalLatencyMs = (long)(snap.AvgLatencyMs * snap.RequestCount);
            if (snap.State == ProviderState.Cooldown && snap.CooldownRemainingSec is { } s)
            {
                _cooldownUntil = DateTimeOffset.Now.AddSeconds(s);
            }
        }
    }
}
