using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace GlmBalanceTaskbar;

static class Program
{
    /// <summary>
    /// 单实例互斥体 / 窗口恢复事件 / AUMID：改为经 DevMode 派生属性取值（单一派生点，§2.2）。
    /// 不带 --dev 时派生属性返回值与原常量逐字节一致（G6）。生产常量仍保留在
    /// DevMode 内的对照位置；此处不再直接引用常量名。
    /// </summary>
    const string ProductionMutexName = @"Local\GlmBalanceTaskbar.SingleInstance";
    const string ProductionShowEventName = @"Local\GlmBalanceTaskbar.ShowWindow";

    /// <summary>
    /// 显式 AUMID：固定到任务栏的快捷方式携带相同 ID 时，运行实例的动态图标（数字）才会被
    /// 固定按钮采用（隐式 exe 路径匹配时 Win11 固定按钮只显示静态快捷方式图标）。
    /// </summary>
    const string ProductionAppUserModelId = "AlisL.GlmBalanceTaskbar";

    /// <summary>编译期防漂移护栏：生产派生值与上方原常量逐字节一致（G6 走查锚点）。</summary>
    [Conditional("DEBUG")]
    static void AssertProductionIdentifiersUnchanged()
    {
        if (!DevMode.IsDev)
        {
            if (!DevMode.MutexName.Equals(ProductionMutexName, StringComparison.Ordinal)) throw new InvalidOperationException("MutexName 漂移");
            if (!DevMode.ShowEventName.Equals(ProductionShowEventName, StringComparison.Ordinal)) throw new InvalidOperationException("ShowEventName 漂移");
            if (!DevMode.Aumid.Equals(ProductionAppUserModelId, StringComparison.Ordinal)) throw new InvalidOperationException("AUMID 漂移");
        }
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appID);

    [STAThread]
    static int Main(string[] args)
    {
        // --dev 必须最先解析（在 --once / --dump-icon / --relay-init 判定之前，组合语义才生效）
        DevMode.Initialize(args);
        AssertProductionIdentifiersUnchanged();

        if (args.Any(a => a.Equals("--once", StringComparison.OrdinalIgnoreCase)))
        {
            return RunOnce();
        }
        if (args.Any(a => a.Equals("--dump-icon", StringComparison.OrdinalIgnoreCase)))
        {
            return RunDumpIcon();
        }
        // 无头首启初始化：--relay-init <dir> 对 <dir>\glmbalance-taskbar.json 执行与 UI
        // 「启用中转」开关完全相同的初始化例程（不监听端口、不占用单实例互斥、完成后退出）
        var relayInitIdx = Array.FindIndex(args, a => a.Equals("--relay-init", StringComparison.OrdinalIgnoreCase));
        if (relayInitIdx >= 0 && relayInitIdx + 1 < args.Length)
        {
            return RunRelayInit(args[relayInitIdx + 1]);
        }

        ApplicationConfiguration.Initialize();
        TrySetAppUserModelId();

        using var mutex = new Mutex(true, DevMode.MutexName, out bool createdNew);
        if (!createdNew)
        {
            // 已有实例：通知其恢复窗口后退出
            try
            {
                using var evt = EventWaitHandle.OpenExisting(DevMode.ShowEventName);
                evt.Set();
            }
            catch
            {
                /* ignore */
            }
            return 0;
        }

        // dev：配置读 %LOCALAPPDATA%\GlmBalanceTaskbar-dev\（生产 exe 同目录那份零读写）；生产：现状
        var cfg = AppConfig.Load(DevMode.IsDev ? DevMode.ConfigRoot : null);
        if (DevMode.IsDev)
        {
            // dev 配置合成（m6 统一单一入口）：mock 端口合成 → relay 默认端口 → monitorBase 覆写 → 预告
            DevMode.ApplyMockOverrides(cfg);
            cfg.Warnings.AddRange(DevMode.DrainWarnings());

            // m3：进程内 mock upstream（8789）生命周期显式化——保存静态引用，Main 退出时 Dispose。
            // 失败 → 仅记录 warning 继续（relay Start() 有 "mock 未运行拒绝启动" 前置，
            // 宁可失败不可打真实上游，§5.5）；托盘/配额轮询同被打到 mock 地址（失败即显示错误）。
            try
            {
                DevMockServer = new MockUpstreamServer(cfg.DevMock);
                DevMockServer.Start(cfg.Relay?.Port ?? DevMode.DefaultRelayPort, cfg.Warnings);
            }
            catch (Exception ex)
            {
                cfg.Warnings.Add($"[dev] mock upstream 启动失败（端口 {DevMode.MockUpstreamPort}）：{ex.Message}");
            }
        }
        try
        {
            using var showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, DevMode.ShowEventName);
            var ctx = new AppContext(cfg);
            var watcher = new Thread(() =>
            {
                while (showEvt.WaitOne())
                {
                    ctx.ShowMainWindow();
                }
            })
            {
                IsBackground = true,
            };
            watcher.Start();

            Application.Run(ctx);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "glmbalance-tray-crash.log"), ex.ToString()); } catch { /* ignore */ }
            throw;
        }
        finally
        {
            DevMockServer?.Dispose(); // m3：显式释放 mock upstream（AppContext.Dispose 已先停 relay）
        }
        GC.KeepAlive(mutex);
        return 0;
    }

    /// <summary>声明进程级显式 AUMID（须在创建任何窗口之前调用）；失败时退回隐式标识，不影响运行。</summary>
    static void TrySetAppUserModelId()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(DevMode.Aumid);
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>
    /// 无头首启初始化（--relay-init &lt;dir&gt;）：对 &lt;dir&gt;\glmbalance-taskbar.json 执行与 UI
    /// 「启用中转」开关完全相同的初始化例程。目标文件独立于 exe 同目录配置读取；不监听端口，
    /// 可在 live 实例运行时安全执行。退出码：0=成功（含幂等跳过），1=失败。
    /// </summary>
    static int RunRelayInit(string dir)
    {
        AttachParentConsole();
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"error: 创建目录失败 {dir}: {ex.Message}");
            return 1;
        }

        // M3 生产目录守卫（宁严勿松，无 --force 逃生舱）：--dev --relay-init 误指生产配置目录
        // 或目标目录含生产配置文件时直接拒绝——避免无备份覆写生产配置。
        if (DevMode.IsDev)
        {
            var prodDir = Path.GetDirectoryName(Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0]) ?? ".";
            var target = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var prodFull = Path.GetFullPath(prodDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(target, prodFull, StringComparison.OrdinalIgnoreCase) ||
                (File.Exists(Path.Combine(target, "glmbalance-taskbar.json")) && IsUnder(target, prodFull)))
            {
                Console.WriteLine("error: --dev --relay-init 禁止指向生产配置目录（exe 同目录）或其中含生产配置文件的目录；请改用 %LOCALAPPDATA%\\GlmBalanceTaskbar-dev");
                return 1;
            }
        }

        var cfgPath = Path.Combine(dir, "glmbalance-taskbar.json");
        // 仅取全局 apiKey 读取链；relay 段按目标文件独立解析。dev 下传 ConfigRoot：
        // 避免读 exe 同目录生产配置，避免真实全局 key 成为 ResolveKey 回退来源（R1-F5）。
        // m1（注释与实现一致）：dev 下该读取链仍可命中真实全局 key（auth.json 回退链只读），
        // key 仅可能作为 Authorization 头发往 127.0.0.1 mock（无外泄面），不断链。
        var cfg = AppConfig.Load(DevMode.IsDev ? DevMode.ConfigRoot : null);
        var warnings = new List<string>();

        RelayOptions? targetOpts = null;
        try
        {
            if (File.Exists(cfgPath))
            {
                using var stream = File.OpenRead(cfgPath);
                using var doc = System.Text.Json.JsonDocument.Parse(stream);
                targetOpts = RelayOptions.Parse(doc.RootElement, warnings);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"读取 {cfgPath} 失败：{ex.Message}");
        }

        var opts = targetOpts ?? RelayOptions.CreateDefault();
        // dev 后处理（R1-F7/N3）：CreateDefault/SeedDefaults 产物与目标目录既有配置两种分支
        // 统一规格化为 dev 语义（port=8788、baseURL→mock、假 key），产物不含真实上游 URL；
        // SeedDefaults 本体不改（生产语义）。此后再走既有 InitHeadless（含 re-enable 分支）。
        if (DevMode.IsDev)
        {
            DevMode.ApplyDevRelayInitSemantics(opts, warnings);
        }
        var mgr = new RelayManager(cfg, opts, configPath: cfgPath, initialized: targetOpts != null);
        var ok = mgr.InitHeadless(warnings);
        foreach (var w in warnings)
        {
            Console.WriteLine("warn: " + w);
        }
        if (!ok)
        {
            Console.WriteLine("FAIL");
            return 1;
        }

        // 骨架回显（token 掩码，不打印全文）
        Console.WriteLine($"relay-init: {cfgPath}");
        Console.WriteLine($"  enabled={opts.Enabled} port={opts.Port} localToken={FmtTokenState(opts.LocalToken)}");
        foreach (var p in opts.Providers)
        {
            Console.WriteLine($"  - {p.Id} ({p.Name}) priority={p.Priority} probe={p.Probe} apiKey={(p.ApiKey.Length > 0 ? "非空" : "空")} models=[{string.Join(", ", p.Models)}] enabled={p.Enabled}");
        }
        Console.WriteLine("OK");
        return 0;
    }

    /// <summary>控制台模式：抓取一次并打印解析结果（对齐 scripts/e2e-check.ts 的输出）。
    /// dev（R1-F4）：不构造 GlmApiClient、不打任何网络——用量/活跃度用内置 mock 快照，纯离线评估。</summary>
    static int RunOnce()
    {
        AttachParentConsole();

        var cfg = AppConfig.Load(DevMode.IsDev ? DevMode.ConfigRoot : null);
        if (DevMode.IsDev)
        {
            // m6：与 Main 一致——单一入口 ApplyMockOverrides（内部端口合成最先）
            DevMode.ApplyMockOverrides(cfg);
        }
        Console.WriteLine($"config: base={cfg.MonitorBase} org={Mask(cfg.Organization)} project={Mask(cfg.Project)} type={CfgStr(cfg.UsageType?.ToString())} interval={cfg.IntervalSeconds}s activity={cfg.Activity}");
        foreach (var w in cfg.Warnings) Console.WriteLine($"warn: {w}");

        if (cfg.Relay is { } relay)
        {
            Console.WriteLine(
                $"relay: enabled={relay.Enabled} port={relay.Port} probe={relay.ProbeIntervalSeconds}s " +
                $"degrade<{relay.DegradeBelowPct}% restore>={relay.RestoreAbovePct}% " +
                $"cooldown(quota={relay.Cooldown.QuotaSeconds}s err={relay.Cooldown.ErrorThreshold}次/{relay.Cooldown.ErrorSeconds}s) " +
                $"token={FmtTokenState(relay.LocalToken)}");
            foreach (var p in relay.Providers)
            {
                var keyState = p.ApiKey.Length > 0 ? "独立 key" : "复用全局";
                Console.WriteLine($"  - {p.Id} ({p.Name}) priority={p.Priority} probe={p.Probe} key={keyState} models=[{string.Join(", ", p.Models)}] enabled={p.Enabled}");
            }
        }
        else
        {
            Console.WriteLine("relay: 未配置（中转关闭，原功能不受影响）");
        }

        UsageSnapshot snap;
        ActivityInfo? devActivity = null;
        if (DevMode.IsDev)
        {
            // --dev --once：内置 mock 快照（不打网络，验收 9）
            snap = DevMode.MockSnapshot(cfg);
            devActivity = cfg.Activity ? DevMode.MockActivity() : null;
        }
        else
        {
            using var client = new GlmApiClient(cfg);
            snap = client.FetchUsageAsync().GetAwaiter().GetResult();
        }
        if (snap.Error != null)
        {
            Console.WriteLine($"error: {snap.Error}");
            return 1;
        }

        Console.WriteLine("quota limits: " + (snap.RawLimits.Count > 0 ? string.Join(", ", snap.RawLimits) : "(空)"));
        Console.WriteLine($"level: {snap.Level ?? "-"}");
        Console.WriteLine($"5h  : {FmtLimit(snap.FiveHour)}");
        Console.WriteLine($"week: {FmtLimit(snap.Week)}");
        Console.WriteLine($"mcp : {FmtLimit(snap.Mcp)}");
        var tokens24h = snap.Tokens24h is long t ? ActivityParser.FmtTokens(t) + " tokens" : "-";
        Console.WriteLine($"24h : {tokens24h}");
        if (snap.Models.Count > 0)
        {
            Console.WriteLine("models: " + string.Join(", ", snap.Models.Select(m => $"{m.Name} {ActivityParser.FmtTokens(m.Tokens)}")));
        }

        if (cfg.Relay is { Enabled: true } relayOn && snap.Error == null)
        {
            // 渠道状态评估（复用刚抓的快照做探测缓存；不监听端口、不写文件）
            Console.WriteLine("relay channels:");
            foreach (var line in RelayManager.EvaluateOnce(cfg, relayOn, snap))
            {
                Console.WriteLine(line);
            }
        }

        if (cfg.Activity)
        {
            if (DevMode.IsDev)
            {
                var act = devActivity;
                if (act != null)
                {
                    var total = act.TotalTokens is long tt ? ActivityParser.FmtTokens(tt) : "?";
                    var today = act.TodayTokens is long td ? ActivityParser.FmtTokens(td) : "?";
                    var streak = act.CurrentStreakDays?.ToString() ?? "?";
                    Console.WriteLine($"activity: [mock] 累计 {total} · {act.DurationLabel ?? "-"} | 今日 {today} | 连续 {streak} 天 | 近14天 {act.Spark ?? "-"}");
                }
            }
            else
            {
                using var client = new GlmApiClient(cfg);
                var act = client.FetchActivityAsync().GetAwaiter().GetResult();
                if (act != null)
                {
                    var total = act.TotalTokens is long tt ? ActivityParser.FmtTokens(tt) : "?";
                    var today = act.TodayTokens is long td ? ActivityParser.FmtTokens(td) : "?";
                    var streak = act.CurrentStreakDays?.ToString() ?? "?";
                    Console.WriteLine($"activity: 累计 {total} · {act.DurationLabel ?? "-"} | 今日 {today} | 连续 {streak} 天 | 近14天 {act.Spark ?? "-"}");
                }
                else
                {
                    Console.WriteLine("activity: 获取失败");
                }
            }
        }
        Console.WriteLine("OK");
        return 0;
    }

    /// <summary>M3 辅助：target 是否位于 under 目录之内（含相等，路径规范化后按分隔符边界比较）。</summary>
    static bool IsUnder(string target, string under)
    {
        var t = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var u = under.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (u.Length == 0) return false;
        return t.Equals(u, StringComparison.OrdinalIgnoreCase) ||
               t.Length > u.Length &&
               (t[u.Length] == Path.DirectorySeparatorChar || t[u.Length] == Path.AltDirectorySeparatorChar) &&
               t.StartsWith(u, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>m3：dev mock upstream 生命周期（进程内单实例；Main/RunOnce 结束时 Dispose）。</summary>
    static MockUpstreamServer? DevMockServer;

    /// <summary>诊断：渲染实验矩阵 + "36" 各尺寸帧写到 %TEMP%。</summary>
    static int RunDumpIcon()
    {
        AttachParentConsole();
        var dir = Path.Combine(Path.GetTempPath(), "glmbalance-icon");
        IconRenderer.DumpFrames("36", Colors.Warning, dir);
        Console.WriteLine($"dumped to: {dir}");
        foreach (var f in Directory.GetFiles(dir, "*.*"))
        {
            Console.WriteLine($"  {Path.GetFileName(f)}  {new FileInfo(f).Length} bytes");
        }
        return 0;
    }

    static string FmtLimit(LimitInfo? l)
    {
        if (l == null) return "-";
        var s = (l.Remaining?.ToString(CultureInfo.InvariantCulture) ?? "?") + "%";
        if (l.Detail != null) s += $" · {l.DetailLabel ?? "已用"} {l.Detail}";
        if (l.ResetLabel != null) s += $" · ↻ {l.ResetLabel}";
        return s;
    }

    static string Mask(string? s) => s == null ? "-" : s.Length <= 12 ? s : s[..12] + "…";

    static string FmtTokenState(string token) =>
        string.IsNullOrWhiteSpace(token) ? "自动生成（首启写回）" : "已配置（…" + token[^6..] + ")";

    static string CfgStr(string? s) => s ?? "-";

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint dwProcessId);

    /// <summary>附加父进程控制台并重建标准输出（兼容管道重定向场景），使 WinExe 在 --once 模式下能看到输出。</summary>
    static void AttachParentConsole()
    {
        const uint AttachParentProcess = 0xFFFFFFFF;
        try
        {
            AttachConsole(AttachParentProcess);
        }
        catch
        {
            /* ignore */
        }
        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch
        {
            /* ignore */
        }
    }
}
