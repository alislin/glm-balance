using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace GlmBalanceTaskbar;

/// <summary>
/// 中转组合根：启停 RelayServer、探测调度、请求日志、配置持久化（唯一写路径）；
/// UI（托盘/详情窗）与 /status、/admin 共读 Snapshot。
/// </summary>
public sealed class RelayManager : IDisposable
{
    readonly AppConfig _cfg;
    readonly RelayOptions _opts;
    readonly string _configPath;
    readonly object _persistLock = new();
    readonly object _probeLock = new();
    readonly object _providersLock = new(); // F6：providers 池配置的读写锁（AdminApi 写操作与快照读）
    readonly object _statsLock = new();     // F5：今日计数与日期翻转
    readonly object _snapshotLock = new();

    ChannelRouter _router = new();
    RequestLog _log = new();
    RelayServer? _server;
    CancellationTokenSource? _probeCts;
    ProbeContext _probeCtx;
    DateTimeOffset? _startedAt;
    long _todayRequests;
    long _todayFailovers;
    DateTimeOffset _statsDate = DateTimeOffset.Now.Date;
    /// <summary>localToken 生成后写回失败 → 免鉴权运行。</summary>
    bool _authDisabled;
    /// <summary>--once 离线评估模式：禁止任何文件写回。</summary>
    bool _offline;

    public RelayManager(AppConfig cfg, RelayOptions opts, string? configPath = null)
    {
        _cfg = cfg;
        _opts = opts;
        _configPath = configPath ?? Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0]) ?? ".",
            "glmbalance-taskbar.json");
        _probeCtx = new ProbeContext
        {
            SnapshotProvider = () => _lastSnap,
            FreshSeconds = () => _opts.ProbeIntervalSeconds + 10,
            Global = cfg,
        };
    }

    public RelayOptions Options => _opts;
    public ChannelRouter Router => _router;
    public bool Running { get; private set; }
    public string AdminUrl => _opts.AdminUrl;
    public string BaseUrl => _opts.BaseUrl;

    /// <summary>生效中的本地 token（免鉴权模式为空串）。</summary>
    public string EffectiveToken => _authDisabled ? "" : _opts.LocalToken;

    UsageSnapshot? _lastSnap;

    /// <summary>key 解析规则（H1-F2/M1）：provider 显式 key 优先；仅 zhipu 域渠道或 zhipu-monitor 探测渠道允许回落全局读取链（该链是智谱定向的），否则视为无 key。</summary>
    internal string? ResolveKey(ProviderConfig p)
    {
        var explicitKey = NonEmpty(p.ApiKey);
        if (explicitKey != null) return explicitKey;
        var isZhipuHost = NonEmpty(p.BaseUrl) is { } url &&
            Uri.TryCreate(url, UriKind.Absolute, out var u) &&
            (u.Host.Contains("bigmodel.cn", StringComparison.OrdinalIgnoreCase) ||
             u.Host.Contains("z.ai", StringComparison.OrdinalIgnoreCase) ||
             u.Host.EndsWith("bigmodel.cn", StringComparison.OrdinalIgnoreCase));
        var isZhipuProbe = string.Equals(p.Probe?.Trim(), "zhipu-monitor", StringComparison.OrdinalIgnoreCase);
        return (isZhipuHost || isZhipuProbe) ? NonEmpty(_cfg.ApiKey) : null;
    }

    /// <summary>构建渠道池：解析实际 key（含回退规则），并注入归位重解析委托。</summary>
    List<ProviderRuntime> BuildPool()
    {
        var pool = new List<ProviderRuntime>();
        foreach (var p in _opts.Providers)
        {
            var rt = new ProviderRuntime(p, _opts, ResolveKey);
            rt.SetResolvedKey(ResolveKey(p));
            pool.Add(rt);
        }
        return pool;
    }

    static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>启动中转：token/播种写回 → 建池 → 启动监听 → 启动探测调度。失败时返回 false（warnings 已带原因）。</summary>
    public bool Start(List<string> warnings)
    {
        if (Running) return true;
        if (!_opts.Enabled) return false;

        // 1) 首启生成随机 token（32 字节 hex）写回；失败 → 警告并免鉴权运行
        if (string.IsNullOrWhiteSpace(_opts.LocalToken))
        {
            _opts.LocalToken = GenerateToken();
            var ok = Persist(relay =>
            {
                relay["localToken"] = _opts.LocalToken;
                return relay;
            }, warnings);
            if (!ok)
            {
                _authDisabled = true;
                warnings.Add("localToken 写回失败：中转以免鉴权模式运行（仅本机回环监听）");
            }
        }

        // 2) providers 缺失播种 → 写回（Parse 已播种；整节点重序列化保证 providers 落盘）
        if (_opts.Seeded)
        {
            Persist(relay => _opts.ToJson(), warnings);
            warnings.Add("relay.providers 缺失：已自动播种默认渠道（zhipu-coding / custom-relay）并写回配置");
            _opts.Seeded = false;
        }

        // 3) 建池 + 启动监听
        _router.ReplacePool(BuildPool());
        _server = new RelayServer(this);
        if (!_server.Start(_opts.Port, warnings))
        {
            _server.Dispose();
            _server = null;
            return false;
        }

        // 4) 探测调度
        _probeCts = new CancellationTokenSource();
        foreach (var rt in _router.Pool())
        {
            _ = Task.Run(() => ProbeLoopAsync(rt, _probeCts.Token));
        }

        _startedAt = DateTimeOffset.Now;
        _todayRequests = 0;
        _todayFailovers = 0;
        _statsDate = DateTimeOffset.Now.Date;
        Running = true;
        return true;
    }

    public void Stop()
    {
        Running = false;
        try { _probeCts?.Cancel(); } catch { /* ignore */ }
        try { _probeCts?.Dispose(); } catch { /* ignore */ }
        _probeCts = null;
        _server?.Dispose();
        _server = null;
    }

    public void Dispose() => Stop();

    static string GenerateToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    // ─────────────────────────── 探测调度 ───────────────────────────

    async Task ProbeLoopAsync(ProviderRuntime rt, CancellationToken ct)
    {
        // 首轮立即探测，其后按 probeIntervalSeconds 独立调度
        var interval = TimeSpan.FromSeconds(_opts.ProbeIntervalSeconds);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProbeOnceAsync(rt, ct).ConfigureAwait(false);
            }
            catch
            {
                /* 探测失败不影响主流程 */
            }
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            interval = TimeSpan.FromSeconds(_opts.ProbeIntervalSeconds); // 设置热更新
        }
    }

    async Task ProbeOnceAsync(ProviderRuntime rt, CancellationToken ct)
    {
        var probe = ProbeRegistry.Resolve(rt.Config.Probe, _probeCtx);
        var result = await probe.ProbeAsync(rt.Config, ct).ConfigureAwait(false);
        rt.ApplyProbe(result, DateTimeOffset.Now);
    }

    /// <summary>AppContext.Apply() 后把最新快照喂给探测缓存。</summary>
    public void FeedSnapshot(UsageSnapshot s)
    {
        _lastSnap = s;
    }

    // ─────────────────────────── 状态快照（UI / status / admin 共读） ───────────────────────────

    /// <summary>日期翻转时清零今日计数（F5：加锁，与请求线程并发安全）。</summary>
    void RollDateIfNeeded_Locked(DateTimeOffset now)
    {
        if (now.Date != _statsDate.Date)
        {
            _todayRequests = 0;
            _todayFailovers = 0;
            _statsDate = now.Date;
        }
    }

    public RelayStatus Snapshot()
    {
        var now = DateTimeOffset.Now;
        lock (_statsLock)
        {
            RollDateIfNeeded_Locked(now);
        }
        var pool = _router.Pool();
        var providers = pool.Select(rt =>
        {
            var ins = rt.Inspect(now);
            return new ProviderStatus(
                rt.Id, rt.Config.Name, rt.Config.Priority, rt.Config.Probe,
                rt.Config.Enabled, HasKey: rt.ResolvedKey != null,
                State: ProviderStateText.Of(ins.State),
                DegradedReason: ins.DegradedReason,
                CooldownReason: ins.CooldownReason,
                CooldownRemainingSec: ins.CooldownRemainingSec,
                ErrorStreak: ins.ErrorStreak,
                RemainingPct: ins.RemainingPct,
                SourceLabel: ins.SourceLabel,
                LastUsedAt: ins.LastUsedAt,
                LastProbeAt: ins.LastProbeAt,
                LastError: ins.LastError,
                RequestCount: ins.RequestCount,
                FailCount: ins.FailCount,
                AvgLatencyMs: ins.AvgLatencyMs);
        }).ToList();

        var current = Running ? _router.Current(now)?.Id : null;
        long req, fail;
        lock (_statsLock)
        {
            req = _todayRequests;
            fail = _todayFailovers;
        }
        return new RelayStatus(
            Running, _opts.Enabled, _opts.Port, current, _startedAt,
            _startedAt is { } st ? (now - st).TotalSeconds : 0,
            req, fail,
            _authDisabled, false,
            providers, _router.ModelUnion());
    }

    static bool Resolved(ProviderRuntime rt) => rt.ResolvedKey != null;

    // ─────────────────────────── 统计与日志 ───────────────────────────

    internal void CountFailover()
    {
        lock (_statsLock)
        {
            RollDateIfNeeded_Locked(DateTimeOffset.Now);
            _todayFailovers++;
        }
    }

    internal void RecordRequest(string model, List<string> chain, int? status, long elapsedMs, string? error)
    {
        lock (_statsLock)
        {
            RollDateIfNeeded_Locked(DateTimeOffset.Now);
            _todayRequests++;
        }
        try
        {
            _log.Add(new RequestLogEntry(DateTimeOffset.Now, model,
                chain.Count > 0 ? chain[^1] : "-", chain, status, elapsedMs, error));
        }
        catch
        {
            /* 日志失败不影响主流程 */
        }
    }

    public List<RequestLogEntry> LogTail(int n) => _log.Tail(n);

    // ─────────────────────────── 持久化（唯一写路径） ───────────────────────────

    /// <summary>合并写回配置文件并让变更生效（重建池、热更新参数）。离线模式拒绝写。</summary>
    public bool PersistAndApply(Func<JsonObject, JsonObject> mutateRelay, List<string>? warnings = null)
    {
        if (_offline) return false;
        bool ok;
        lock (_persistLock)
        {
            ok = RelayOptions.Persist(_configPath, mutateRelay, warnings);
        }
        if (ok) ApplyOptions();
        return ok;
    }

    /// <summary>不写文件、仅把内存选项套用到运行时（池重建、探测间隔/阈值热更新、token 生效）。</summary>
    public void ApplyOptions()
    {
        // 保留同 id 渠道的运行状态与统计
        var old = _router.Pool().ToDictionary(rt => rt.Id, StringComparer.Ordinal);
        var pool = BuildPool();
        foreach (var rt in pool)
        {
            if (old.TryGetValue(rt.Id, out var prev))
            {
                rt.Adopt(prev);
            }
        }
        _router.ReplacePool(pool);
        lock (_probeLock)
        {
            // 探测循环自旋读取 _opts，池替换后旧循环操作的 rt 会被垃圾回收；
            // 为每个新渠道补一轮立即探测（旧循环随 ct 取消）
            if (Running)
            {
                try { _probeCts?.Cancel(); } catch { /* ignore */ }
                try { _probeCts?.Dispose(); } catch { /* ignore */ }
                _probeCts = new CancellationTokenSource();
                foreach (var rt in pool)
                {
                    var captured = rt;
                    _ = Task.Run(() => ProbeLoopAsync(captured, _probeCts.Token));
                }
            }
        }
    }

    /// <summary>仅写文件不重建运行时（token 生成、播种等启动期路径用）。</summary>
    public bool Persist(Func<JsonObject, JsonObject> mutateRelay, List<string>? warnings = null)
    {
        if (_offline) return false;
        lock (_persistLock)
        {
            return RelayOptions.Persist(_configPath, mutateRelay, warnings);
        }
    }

    // ─────────────────────────── 托盘/详情窗摘要 ───────────────────────────

    /// <summary>托盘悬停"中转"行：正常 / 异常 / degraded / 已关闭。已停用与 no-key 渠道不参与首选通道判定（F10）。</summary>
    public string PreviewLine()
    {
        if (!Running) return "中转  已关闭";
        var now = DateTimeOffset.Now;
        var pool = _router.Pool();
        var preferred = pool
            .Where(rt => rt.Config.Enabled && rt.ResolvedKey != null)
            .OrderBy(rt => rt.Config.Priority)
            .FirstOrDefault();
        var current = _router.Current(now);
        if (current == null) return "中转  无可用渠道";
        if (preferred != null && preferred.Id != current.Id)
        {
            var ins = preferred.Inspect(now);
            var reason = ins.State switch
            {
                ProviderState.Cooldown => $"{preferred.Id} 冷却 {ins.CooldownRemainingSec ?? 0}s",
                ProviderState.Degraded when ins.DegradedReason != null => $"degraded：{ins.DegradedReason}",
                ProviderState.Degraded => "degraded",
                _ => $"{preferred.Id} 不可用",
            };
            return $"中转  {current.Id}（{reason}）";
        }
        var cur = current.Inspect(now);
        return cur.State switch
        {
            ProviderState.Normal => $"中转  {current.Id} · 正常",
            ProviderState.Degraded => $"中转  {current.Id} · degraded：{cur.DegradedReason ?? (cur.RemainingPct is { } p ? $"剩余 {p}%" : "用量低")}",
            ProviderState.Cooldown => $"中转  {current.Id} · 冷却中",
            _ => $"中转  {current.Id} · 无 key",
        };
    }

    // ─────────────────────────── admin 支撑 ───────────────────────────

    public List<ProviderRuntime> Pool => _router.Pool();

    /// <summary>providers 配置快照（F6：锁内读取，元素为共享引用）。</summary>
    public List<ProviderConfig> ProvidersSnapshot()
    {
        lock (_providersLock)
        {
            return _opts.Providers.ToList();
        }
    }

    /// <summary>providers 配置变更（F6：串行化 AdminApi 的写操作）。action 返回是否发生实际变更。</summary>
    public bool MutateProviders(Func<List<ProviderConfig>, bool> action)
    {
        lock (_providersLock)
        {
            return action(_opts.Providers);
        }
    }

    /// <summary>聚合全部智能体配置源的发现结果。</summary>
    public async Task<IReadOnlyList<DiscoveredProvider>> DiscoverAsync(CancellationToken ct = default)
    {
        var results = new List<DiscoveredProvider>();
        foreach (var source in CreateConfigSources())
        {
            try
            {
                results.AddRange(await source.DiscoverAsync(ct).ConfigureAwait(false));
            }
            catch
            {
                /* 单个源失败不影响其他源 */
            }
        }
        return results;
    }

    List<IAgentConfigSource> CreateConfigSources() => new()
    {
        new OpenCodeConfigSource(),
        new PiConfigSource(_opts.PiConfigPath),
    };

    /// <summary>部署器列表（opencode 完整实现；pi 占位）。</summary>
    public List<IAgentDeployer> CreateDeployers() => new()
    {
        new OpenCodeDeployer(),
        new PiDeployer(),
    };

    /// <summary>测试连接：GET {baseURL}/models（10s 超时；共享静态 client，F7）。</summary>
    static readonly HttpClient TestClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<(bool Ok, string Message)> TestProviderAsync(ProviderRuntime rt, CancellationToken ct)
    {
        var key = rt.ResolvedKey;
        if (key == null) return (false, "无 API key");
        var url = rt.Config.BaseUrl.TrimEnd('/') + "/models";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            using var res = await TestClient.SendAsync(req, ct).ConfigureAwait(false);
            return (int)res.StatusCode switch
            {
                200 => (true, "连接成功"),
                401 or 403 => (false, $"鉴权失败（HTTP {(int)res.StatusCode}）"),
                _ => (false, $"HTTP {(int)res.StatusCode}"),
            };
        }
        catch (Exception ex)
        {
            return (false, "失败：" + ex.Message);
        }
    }

    // ─────────────────────────── --once 离线评估 ───────────────────────────

    /// <summary>--once 模式：不监听端口不写文件，仅评估当前配置下的渠道状态（复用已抓快照做探测缓存）。</summary>
    public static List<string> EvaluateOnce(AppConfig cfg, RelayOptions opts, UsageSnapshot snapshot)
    {
        var lines = new List<string>();
        var mgr = new RelayManager(cfg, opts, configPath: "") { _offline = true, _lastSnap = snapshot };
        var pool = mgr.BuildPool();
        foreach (var rt in pool)
        {
            if (!opts.Enabled)
            {
                lines.Add($"  · {rt.Id}: 已关闭（relay.enabled=false）");
                continue;
            }
            if (rt.ResolvedKey == null)
            {
                lines.Add($"  · {rt.Id}: no-key（无可用 API key）");
                continue;
            }
            if (rt.Config.Probe.Trim().Equals("zhipu-monitor", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var probe = ProbeRegistry.Resolve(rt.Config.Probe, mgr._probeCtx);
                    var result = probe.ProbeAsync(rt.Config, CancellationToken.None).GetAwaiter().GetResult();
                    rt.ApplyProbe(result, DateTimeOffset.Now);
                }
                catch (Exception ex)
                {
                    lines.Add($"  · {rt.Id}: 探测失败（{ex.Message}）→ 状态保持 normal，反应式兜底");
                    continue;
                }
            }
            var now = DateTimeOffset.Now;
            var ins = rt.Inspect(now);
            var text = ProviderStateText.Of(ins.State);
            if (ins.State == ProviderState.Degraded && ins.DegradedReason != null) text += $"（{ins.DegradedReason}）";
            if (ins.State == ProviderState.Cooldown && ins.CooldownReason != null) text += $"（{ins.CooldownReason} {ins.CooldownRemainingSec}s）";
            if (ins.RemainingPct is { } v) text += $" · 用量剩余 {v}%";
            lines.Add($"  · {rt.Id}: {text}");
        }
        return lines;
    }
}
