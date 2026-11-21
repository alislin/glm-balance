namespace GlmBalanceTaskbar;

/// <summary>
/// --dev 开发模式单一真源：参数解析、派生标识（互斥体/事件/AUMID）、配置根目录、
/// relay 默认端口、mock 上游地址与三层覆写逻辑。
/// 不带 --dev 时所有派生值与生产常量逐字节一致、全部方法短路返回，生产代码路径零新增行为分支（G6）。
/// 设计依据：docs/plans/dev-prod-separation.md（v2，R2 APPROVED）。
/// </summary>
public static class DevMode
{
    /// <summary>dev 模式判定（Main 最早解析，仅一次；--dev 精确匹配，OrdinalIgnoreCase，与既有参数同风格）。</summary>
    public static bool IsDev { get; private set; }

    /// <summary>生产默认 relay 端口（dev 实例硬禁监听）。</summary>
    public const int ProductionRelayPort = 8787;
    /// <summary>dev 实例 relay 默认监听端口（dev 配置可显式覆盖其它端口；显式写 8787 由 Start() 硬禁拦截）。</summary>
    public const int DefaultRelayPort = 8788;
    /// <summary>进程内 mock 上游默认端口。</summary>
    public const int DefaultMockUpstreamPort = 8789;

    /// <summary>开发实例配置根目录（与生产 exe 目录、仓库目录彻底解耦）。</summary>
    public static string ConfigRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GlmBalanceTaskbar-dev");

    /// <summary>dev 配置文件路径（与生产同名、不同目录，复用全部解析/写回代码）。</summary>
    public static string ConfigPath => Path.Combine(ConfigRoot, "glmbalance-taskbar.json");

    // ── 派生标识：生产值逐字节不变，dev 加 .Dev 后缀（单一派生点） ──

    public static string MutexName => IsDev
        ? @"Local\GlmBalanceTaskbar.SingleInstance.Dev"
        : @"Local\GlmBalanceTaskbar.SingleInstance";

    public static string ShowEventName => IsDev
        ? @"Local\GlmBalanceTaskbar.ShowWindow.Dev"
        : @"Local\GlmBalanceTaskbar.ShowWindow";

    public static string Aumid => IsDev
        ? "AlisL.GlmBalanceTaskbar.Dev"
        : "AlisL.GlmBalanceTaskbar";

    /// <summary>mock 上游端口（dev 配置 devMock.upstreamPort 可经 SetMockUpstreamPort 覆盖；默认 8789）。
    /// 必须在首次建池/启动 mock 之前合成，此后建池覆写与自环校验共用。</summary>
    public static int MockUpstreamPort { get; private set; } = DefaultMockUpstreamPort;

    public static string MockBaseUrl => $"http://127.0.0.1:{MockUpstreamPort}";

    /// <summary>mock 上游是否已在运行（dev 下 relay Start() 的前置条件：宁可失败不可打真实上游）。</summary>
    public static bool MockUpstreamRunning { get; private set; }

    static bool _initialized;

    public static void Initialize(string[] args)
    {
        if (_initialized) return;
        _initialized = true;
        IsDev = args.Any(a => a.Equals("--dev", StringComparison.OrdinalIgnoreCase));
    }

    public static void SetMockUpstreamPort(int port)
    {
        if (port is > 0 and <= 65535) MockUpstreamPort = port;
    }

    public static void SetMockUpstreamRunning(bool running) => MockUpstreamRunning = running;

    /// <summary>mock 上游地址判定（建池覆写与配置覆写共用）：绝对 URI 且 Host 恰为 127.0.0.1、端口恰为 mock 端口。</summary>
    public static bool IsMockBaseUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        u.Host == "127.0.0.1" &&
        u.Port == MockUpstreamPort;

    // ── 第 1 层覆写（配置合成阶段，便利层） ──

    /// <summary>
    /// 启动配置合成后的 dev 覆写：mock 端口合成 + 全局 MonitorBase 指向 mock 上游（托盘/GlmApiClient 出口）
    /// + 渠道覆写预告 warning。注意：不就地改写 providers 的 BaseUrl——出站隔离由第 2 层
    /// （BuildPool 克隆后覆写，N2）独立保证，_opts.Providers 保持用户原值（全节点写回忠实往返）。
    /// </summary>
    public static void ApplyMockOverrides(AppConfig? cfg)
    {
        if (!IsDev || cfg == null) return;
        if (cfg.DevMock != null) SetMockUpstreamPort(cfg.DevMock.UpstreamPort);

        if (!IsMockBaseUrl(cfg.MonitorBase))
        {
            var original = cfg.MonitorBase;
            cfg.MonitorBase = MockBaseUrl;
            cfg.Warnings.Add($"[dev] monitorBase 已覆写为 mock 上游 {MockBaseUrl}（原值：{original}）");
        }

        var providers = cfg.Relay?.Providers;
        if (providers is { Count: > 0 })
        {
            var real = providers.Where(p => !IsMockBaseUrl(p.BaseUrl)).Select(p => p.Id).ToList();
            if (real.Count > 0)
            {
                cfg.Warnings.Add($"[dev] {real.Count} 个渠道上游将在建池时覆写为 mock {MockBaseUrl}" +
                                 $"（{string.Join(", ", real)}；配置文件保留原值）");
            }
        }
    }

    // ── dev relay 默认端口合成 ──

    /// <summary>dev 下 relay.port 未显式配置且为生产默认 8787 → 置 8788。
    /// 显式写 8787 不在此改写（保留用户写入），由 RelayManager.Start() 硬禁拦截（验收 3b 的拦截时点）。</summary>
    public static void ApplyDevRelayDefaults(AppConfig? cfg)
    {
        if (!IsDev || cfg == null) return;
        ApplyDevRelayDefaults(cfg.Relay, cfg.Warnings);
    }

    public static void ApplyDevRelayDefaults(RelayOptions? opts, List<string>? warnings)
    {
        if (!IsDev || opts == null) return;
        if (!opts.PortExplicit && opts.Port == ProductionRelayPort)
        {
            opts.Port = DefaultRelayPort;
            warnings?.Add($"relay.port 未配置：dev 实例默认使用 {DefaultRelayPort}（生产端口 {ProductionRelayPort} 已硬禁）");
        }
    }

    // ── 第 2 层覆写（建池时强制，最终防线，R1-F1/N2） ──

    static readonly HashSet<string> _poolWarned = new(StringComparer.Ordinal);
    static readonly object _warnGate = new();
    static readonly List<string> _pendingWarnings = new();

    /// <summary>
    /// 建池时单渠道覆写（BuildPool 对克隆调用，N2）：非 mock baseURL → 内存覆写为 mock 上游。
    /// warning 按渠道 id 去重（每进程一次），经 DrainWarnings 并入详情窗/控制台展示。
    /// </summary>
    public static void ApplyPoolOverride(ProviderConfig? p)
    {
        if (!IsDev || p == null || IsMockBaseUrl(p.BaseUrl)) return;
        var original = p.BaseUrl;
        p.BaseUrl = MockBaseUrl;
        lock (_warnGate)
        {
            if (!_poolWarned.Add(p.Id)) return;
        }
        AddWarning($"[dev] 渠道 {p.Id} baseURL 已覆写为 mock 上游 {MockBaseUrl}（配置文件保留原值：{original}）");
    }

    static void AddWarning(string w)
    {
        lock (_warnGate) _pendingWarnings.Add(w);
    }

    /// <summary>取走并清空 dev 侧累积警告（详情窗 / --once 控制台消费）。</summary>
    public static List<string> DrainWarnings()
    {
        lock (_warnGate)
        {
            var list = new List<string>(_pendingWarnings);
            _pendingWarnings.Clear();
            return list;
        }
    }

    // ── --dev --once：内置 mock 快照（R1-F4，RunOnce 不构造 GlmApiClient、不打网络） ──

    public static UsageSnapshot MockSnapshot(AppConfig cfg)
    {
        var q5 = Math.Clamp(cfg.DevMock?.FiveHourRemainingPct ?? 80, 0, 100);
        var qw = Math.Clamp(cfg.DevMock?.WeekRemainingPct ?? 60, 0, 100);
        return new UsageSnapshot
        {
            Level = "mock",
            FiveHour = new LimitInfo("5h 积分", q5, $"{(100 - q5) * 150}/15000", "已用", null),
            Week = new LimitInfo("周积分", qw, $"{(100 - qw) * 660}/66000", "已用", null),
            Tokens24h = 1_234_567,
            Models = new List<ModelUsage> { new("glm-5.3-flash", 1_234_567) },
            RawLimits = new List<string>
            {
                $"CREDIT_LIMIT/unit=3/{100 - q5}%",
                $"CREDIT_LIMIT/unit=6/{100 - qw}%",
            },
        };
    }

    public static ActivityInfo MockActivity() => new()
    {
        TotalTokens = 3_520_000_000,
        DurationLabel = "mock 149h",
        CurrentStreakDays = 25,
        LongestStreakDays = 40,
        TodayTokens = 59_000_000,
        PeakTokens = 59_000_000,
        PeakDate = "11-20",
        Spark = "▁▂▃▄▅▆▇█▆▅▄▃▂▁",
        DailyTokens = new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 7, 6, 5, 4, 3, 2 },
    };

    // ── --dev --relay-init 的 dev 语义后处理（R1-F7/N3：覆盖全新目录与既有配置两分支） ──

    /// <summary>
    /// 对 CreateDefault()/SeedDefaults() 产物或目标目录既有 relay 配置做 dev 语义规格化：
    /// port=8788；providers 的非 mock baseURL 全部覆写为 mock 上游、空/mock key 播种为
    /// "mock-&lt;id&gt;" 假 key（既有真实显式 key 保留——它只会发往 127.0.0.1，避免破坏用户存量）。
    /// 两种分支的产物均不含真实上游 URL。
    /// </summary>
    public static void ApplyDevRelayInitSemantics(RelayOptions? opts, List<string>? warnings)
    {
        if (!IsDev || opts == null) return;
        opts.Port = DefaultRelayPort;
        foreach (var p in opts.Providers)
        {
            if (!IsMockBaseUrl(p.BaseUrl)) p.BaseUrl = MockBaseUrl;
            if (string.IsNullOrWhiteSpace(p.ApiKey) || p.ApiKey.StartsWith("mock-", StringComparison.Ordinal))
            {
                p.ApiKey = "mock-" + p.Id;
            }
        }
        warnings?.Add($"[dev] relay-init dev 语义：port={DefaultRelayPort}、providers baseURL→{MockBaseUrl}、空 key 播种为 mock-<id> 假 key");
    }
}
