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

    public RelayManager(AppConfig cfg, RelayOptions opts, string? configPath = null, bool initialized = true)
    {
        _cfg = cfg;
        _opts = opts;
        Initialized = initialized;
        // B2：configPath 未显式传参时经统一派生（dev → ConfigRoot，生产 → exe 同目录）。
        // DevMode.IsDev 在 Main 最早已解析，时序安全；dev 实例任何缺省派生都不可能落到生产配置文件。
        _configPath = configPath ?? DefaultConfigPath();
        _probeCtx = new ProbeContext
        {
            SnapshotProvider = () => _lastSnap,
            FreshSeconds = () => _opts.ProbeIntervalSeconds + 10,
            Global = cfg,
        };
    }

    /// <summary>dev 统一配置根：--dev 时配置文件派生自 %LOCALAPPDATA%\GlmBalanceTaskbar-dev\，否则 exe 同目录（现状）。</summary>
    internal static string DefaultConfigPath() =>
        DevMode.IsDev
            ? DevMode.ConfigPath
            : Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0]) ?? ".",
                "glmbalance-taskbar.json");

    public RelayOptions Options => _opts;
    public ChannelRouter Router => _router;
    public bool Running { get; private set; }
    /// <summary>配置文件存在 relay 段（false = 未初始化引导态：等待用户点击「启用中转」）。</summary>
    public bool Initialized { get; }
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

    /// <summary>
    /// 构建渠道池：解析实际 key（含回退规则），并注入归位重解析委托。
    /// dev（R1-F1 最终防线 + N2 克隆后覆写）：对每个 ProviderConfig 克隆一份再 ApplyPoolOverride——
    /// 池持克隆引用（出站必为 mock），_opts.Providers 保持用户原值（全节点写回忠实往返）；
    /// 生产（无 --dev）此分支短路，行为与现状逐字节一致。
    /// </summary>
    List<ProviderRuntime> BuildPool()
    {
        var pool = new List<ProviderRuntime>();
        foreach (var p in _opts.Providers)
        {
            var cfgForPool = p;
            if (DevMode.IsDev)
            {
                cfgForPool = CloneConfig(p);
                DevMode.ApplyPoolOverride(cfgForPool);
            }
            var rt = new ProviderRuntime(cfgForPool, _opts, ResolveKey);
            rt.SetResolvedKey(ResolveKey(cfgForPool));
            pool.Add(rt);
        }
        return pool;
    }

    static ProviderConfig CloneConfig(ProviderConfig p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Priority = p.Priority,
        Probe = p.Probe,
        BaseUrl = p.BaseUrl,
        ApiKey = p.ApiKey,
        Models = p.Models.ToList(),
        Enabled = p.Enabled,
    };

    static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>启动中转：token/播种写回 → 建池 → 启动监听 → 启动探测调度。失败时返回 false（warnings 已带原因）。</summary>
    public bool Start(List<string> warnings)
    {
        if (Running) return true;
        if (!_opts.Enabled) return false;

        // dev 硬防护（G2）：任何时刻禁止 dev 实例占用生产端口 8787（含面板改 port 后重启/重新启用的路径）
        if (DevMode.IsDev && _opts.Port == DevMode.ProductionRelayPort)
        {
            warnings.Add("dev 实例禁止占用生产端口 8787：请修改 relay.port 后重启");
            return false;
        }

        // 0) dev 前置：mock upstream 必须已在运行（宁可失败不可打真实上游，§5.5）
        if (DevMode.IsDev && !DevMode.MockUpstreamRunning)
        {
            warnings.Add($"dev mock upstream（127.0.0.1:{DevMode.MockUpstreamPort}）未运行：拒绝启动 relay" +
                         "（不允许真实上游出站）。请先解决启动 warnings 中 mock upstream 的端口占用");
            return false;
        }

        // 0.5) dev 覆写 warning 并入本次启动 warnings（ApplyMockOverrides 已在 Main 并入 cfg.Warnings，
        // 这里收建池前的 DevMode 静态累积；建池后的在下方针再度并入）
        if (DevMode.IsDev)
        {
            warnings.AddRange(DevMode.DrainWarnings());
        }

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
        if (DevMode.IsDev)
        {
            // 建池覆写 warning（DevMode 静态累积，按渠道 id 去重）并入启动 warnings
            warnings.AddRange(DevMode.DrainWarnings());
        }
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
        var server = _server;
        _server = null;
        if (server != null)
        {
            // 1) 立即停止接受新请求；已受理的 in-flight 响应可继续写完
            server.StopAccepting();
            // 2) 宽限 ≤3s 处理 in-flight，超时由 Shutdown 经取消/异常路径 Abort
            var deadline = Environment.TickCount64 + GracePeriodMs;
            while (server.Inflight > 0 && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(50);
            }
            // 3) 全量关停（listener 关闭 + 请求 CTS 取消）
            server.Shutdown();
        }
        // 探测调度暂停（状态快照保留：Providers 仍在，Running=false）
        try { _probeCts?.Cancel(); } catch { /* ignore */ }
        try { _probeCts?.Dispose(); } catch { /* ignore */ }
        _probeCts = null;
    }

    /// <summary>Stop 时处理 in-flight 请求的宽限上限（毫秒）。</summary>
    const int GracePeriodMs = 3000;

    /// <summary>
    /// --relay-init 无头初始化：执行与 UI「启用中转」开关完全相同的配置生成例程
    /// （relay 段整体生成：enabled=true、port=8787、localToken 自动生成写回、默认参数、
    /// providers 播种 zhipu-coding / custom-relay），但不监听端口、不启动运行时、无 UI。
    /// 幂等：已存在 relay 段且 enabled=true 时跳过；显式关闭时与 UI 开关同语义——
    /// 保留既有 providers/配置，仅置 enabled=true（再次启用）。
    /// </summary>
    public bool InitHeadless(List<string> warnings)
    {
        if (Initialized)
        {
            if (_opts.Enabled)
            {
                // M2：dev 下（ApplyDevRelayInitSemantics 已在 RunRelayInit 规格化内存 opts）
                // 必须全节点写回，使既有目录分支产物同样不含真实上游 URL（声明与实现一致）；
                // 生产保持现状：跳过，保留既有配置。
                if (DevMode.IsDev)
                {
                    var devOk = Persist(relay => _opts.ToJson(), warnings);
                    if (devOk) warnings.Add("relay 段已存在且启用：已按 dev 语义规格化并写回（baseURL→mock、假 key、端口语义）");
                    return devOk;
                }
                warnings.Add("relay 段已存在且启用：跳过初始化（保留既有配置）");
                return true;
            }
            _opts.Enabled = true;
            var reOk = Persist(relay => { relay["enabled"] = true; return relay; }, warnings);
            if (reOk) warnings.Add("中转已置为启用（保留既有 providers/配置，仅 enabled=true）");
            if (reOk && DevMode.IsDev)
            {
                // M2：dev 下既有 providers 是真实 baseURL 时，仅置 enabled 会保留真实上游落盘——
                // 再做一次全节点重写为已规格化的 dev 语义。
                var devReOk = Persist(relay => _opts.ToJson(), warnings);
                if (devReOk) warnings.Add("已按 dev 语义规格化既有 providers 并写回（baseURL→mock、假 key）");
                return devReOk;
            }
            return reOk;
        }
        _opts.Enabled = true;
        if (string.IsNullOrWhiteSpace(_opts.LocalToken))
        {
            _opts.LocalToken = GenerateToken();
        }
        // 与 Start() 的 token/播种写回同一载荷（ToJson 全节点），生成结果与 UI 开关一致
        var ok = Persist(relay => _opts.ToJson(), warnings);
        if (ok)
        {
            _opts.Seeded = false;
            warnings.Add("relay 段已生成：enabled=true、port=8787、localToken 自动生成、providers 播种 zhipu-coding/custom-relay（custom-relay 需在 Provider 池填写地址与 key）");
        }
        return ok;
    }

    /// <summary>
    /// 运行时启停开关（DetailForm 开关与 admin settings 同源语义）：
    /// enable=true → 写回 enabled 并启动（未初始化时经 Start() 的 token/播种路径自动初始化空配置）；
    /// enable=false → 写回 enabled=false 并停止运行时（不删除既有 providers 配置）。
    /// 启动失败（端口占用等）→ 状态回滚为关闭并写回，warnings 带原因，不残留半初始化状态。
    /// </summary>
    public bool SetEnabled(bool enable, List<string> warnings)
    {
        if (enable)
        {
            if (Running) return true; // 重入安全
            _opts.Enabled = true;
            Persist(relay => { relay["enabled"] = true; return relay; }, warnings);
            if (Start(warnings)) return true;
            // 失败回滚：状态回滚为关闭（保留已写回的完整配置段，仅 enabled=false）
            _opts.Enabled = false;
            warnings.Add("中转启用失败：已回滚为关闭状态");
            Persist(relay => { relay["enabled"] = false; return relay; }, warnings);
            return false;
        }
        else
        {
            _opts.Enabled = false;
            var ok = Persist(relay => { relay["enabled"] = false; return relay; }, warnings);
            Stop();
            return ok;
        }
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
                FiveHourPct: ins.FiveHourPct,
                WeekPct: ins.WeekPct,
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
            Running, _opts.Enabled, Initialized, _opts.Port, current, _startedAt,
            _startedAt is { } st ? (now - st).TotalSeconds : 0,
            req, fail,
            _authDisabled, false,
            providers, _router.ModelUnion(),
            Dev: DevMode.IsDev,
            Forced: BuildForcedStatus(now));
    }

    /// <summary>
    /// 组装 sticky pin 强制通道快照（无 pin → null，序列化省略，/status 形状逐字节不变）。
    /// Active = pin 渠道当前可路由（Enabled ∧ 有 key ∧ 状态 Normal）；false 时给中文短因。
    /// </summary>
    ForcedRouteStatus? BuildForcedStatus(DateTimeOffset now)
    {
        var forcedId = _router.ForcedId;
        if (forcedId == null) return null;
        var rt = _router.Pool().FirstOrDefault(p => p.Id == forcedId);
        if (rt == null) return null; // ReplacePool 已防御清 pin，此为极小竞态兜底
        var ins = rt.Inspect(now);
        var active = rt.Config.Enabled && rt.ResolvedKey != null && ins.State == ProviderState.Normal;
        if (active) return new ForcedRouteStatus(forcedId, Active: true, Reason: null);
        var reason = ins.State switch
        {
            ProviderState.Cooldown => $"冷却 {ins.CooldownRemainingSec ?? 0}s",
            ProviderState.Degraded when ins.DegradedReason != null => $"degraded：{ins.DegradedReason}",
            ProviderState.Degraded => "degraded",
            ProviderState.DisabledNoKey => "无 key",
            _ => null,
        };
        if (!rt.Config.Enabled) reason = "已停用";
        return new ForcedRouteStatus(forcedId, Active: false, Reason: reason ?? "不可用");
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

    /// <summary>托盘悬停"中转"行：正常 / 异常 / degraded / 已关闭。已停用与 no-key 渠道不参与首选通道判定（F10）。
    /// pin 分支置于既有 current==null 短路之后（n1：全渠道不可用时先走"无可用渠道"，不 NRE）；
    /// pin 生效直接返回 [强制] 行、跳过既有 preferred≠current 解释分支（m1，不叠加）；
    /// pin 未生效给出"已按自动规则"回退说明；无 pin 时现状逻辑逐字不变。</summary>
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
        var forcedId = _router.ForcedId;
        if (forcedId != null)
        {
            // F4：入口取一次 Inspect 复用全分支（对齐 BuildForcedStatus 单次 Inspect 范式）
            var forcedRt = pool.FirstOrDefault(rt => rt.Id == forcedId);
            var fins = forcedRt?.Inspect(now);
            var forcedActive = forcedRt != null && fins!.State == ProviderState.Normal
                && forcedRt.Config.Enabled && forcedRt.ResolvedKey != null;
            if (forcedActive)
            {
                // pin 生效：current 即 pin 渠道，直接给 [强制] 行，跳过 preferred≠current 解释分支。
                // 仅 Normal 可达（Current 的 pin 候选判 IsRoutable），无其他状态 arm。
                return $"中转  [强制] {forcedId} · 正常";
            }
            // pin 未生效（冷却/降级/停用/极小竞态已移除）→ 让位自动规则，说明原因
            var freason = forcedRt == null
                ? "渠道已移除"
                : !forcedRt.Config.Enabled ? "已停用"
                : fins!.State switch
                {
                    ProviderState.Cooldown => $"冷却 {forcedRt.CooldownRemaining(now) ?? 0}s",
                    ProviderState.Degraded when fins.DegradedReason != null => $"degraded：{fins.DegradedReason}",
                    ProviderState.Degraded => "degraded",
                    _ => "不可用",
                };
            return $"中转  {current.Id}（强制通道 {forcedId} {freason}，已按自动规则）";
        }
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

    /// <summary>
    /// 部署器列表（opencode 完整实现；pi 占位）。
    /// dev 下包 DryRun 装饰器（G5）：部署只预览、绝不落盘；AdminApi 零改动获得 dry-run 语义。
    /// </summary>
    public List<IAgentDeployer> CreateDeployers()
    {
        var list = new List<IAgentDeployer>
        {
            new OpenCodeDeployer(),
            new PiDeployer(),
        };
        if (DevMode.IsDev)
        {
            list = list.Select(d => new DryRunDeployer(d)).ToList<IAgentDeployer>();
        }
        return list;
    }

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

    /// <summary>
    /// --once 模式：不监听端口不写文件，仅评估当前配置下的渠道状态（复用已抓快照做探测缓存）。
    /// dev（R1-F4）：传入的 snapshot 已是 DevMode.MockSnapshot 内置快照（RunOnce 不构造 GlmApiClient），
    /// 全链离线；建池覆写 warning 并入返回行。
    /// </summary>
    public static List<string> EvaluateOnce(AppConfig cfg, RelayOptions opts, UsageSnapshot snapshot)
    {
        var lines = new List<string>();
        var mgr = new RelayManager(cfg, opts, configPath: "") { _offline = true, _lastSnap = snapshot };
        var pool = mgr.BuildPool();
        if (DevMode.IsDev)
        {
            lines.AddRange(DevMode.DrainWarnings());
        }
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
