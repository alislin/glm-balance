using System.Globalization;
using System.Runtime.InteropServices;

namespace GlmBalanceTaskbar;

static class Program
{
    const string MutexName = @"Local\GlmBalanceTaskbar.SingleInstance";
    const string ShowEventName = @"Local\GlmBalanceTaskbar.ShowWindow";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--once", StringComparison.OrdinalIgnoreCase)))
        {
            return RunOnce();
        }
        if (args.Any(a => a.Equals("--dump-icon", StringComparison.OrdinalIgnoreCase)))
        {
            return RunDumpIcon();
        }

        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // 已有实例：通知其恢复窗口后退出
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ShowEventName);
                evt.Set();
            }
            catch
            {
                /* ignore */
            }
            return 0;
        }

        var cfg = AppConfig.Load();
        try
        {
            using var showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
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
        GC.KeepAlive(mutex);
        return 0;
    }

    /// <summary>控制台模式：抓取一次并打印解析结果（对齐 scripts/e2e-check.ts 的输出）。</summary>
    static int RunOnce()
    {
        AttachParentConsole();

        var cfg = AppConfig.Load();
        Console.WriteLine($"config: base={cfg.MonitorBase} org={Mask(cfg.Organization)} project={Mask(cfg.Project)} type={CfgStr(cfg.UsageType?.ToString())} interval={cfg.IntervalSeconds}s activity={cfg.Activity}");
        foreach (var w in cfg.Warnings) Console.WriteLine($"warn: {w}");

        using var client = new GlmApiClient(cfg);
        var snap = client.FetchUsageAsync().GetAwaiter().GetResult();
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

        if (cfg.Activity)
        {
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
        Console.WriteLine("OK");
        return 0;
    }

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
