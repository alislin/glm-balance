using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlmBalanceTaskbar;

/// <summary>
/// headless 模式组合根（--headless，§2.1 方案 A）：开机/后台独立进程承载 RelayServer + 探测 + AdminApi。
/// 不建任何 UI、不做配额轮询；本文件零 WinForms 引用、Main 分派在 WinForms 初始化之前——
/// headless 进程模块列表不含 System.Windows.Forms.dll（R1-F3，验收锚点 A2）。
/// 启动序列（§2.1）：--config-dir 注入 home 重定向 → HeadlessMutex 抢占（失败静默 exit 0）
/// → AppConfig.Load（配置本体恒为 exe 同目录）→ relay 段缺失或 enabled=false → exit 0（绝不占端口，E3）
/// → RelayManager.Start（失败：崩溃日志 + exit 1，E2）→ 工作集裁剪一次 → 驻留（无优雅停机钩子，§1 非目标）。
/// </summary>
static class HeadlessRunner
{
    /// <summary>崩溃日志文件名（沿用 AppContext 模式；SYSTEM 下 %TEMP% = C:\Windows\Temp，可写）。</summary>
    const string CrashLogName = "glmbalance-headless-crash.log";

    public static int Run(string[] args)
    {
        // --config-dir <dir>（用户 profile 根重定向，§2.3 方案 A）：SYSTEM 账户下 key 链
        // （auth.json / opencode.json / tui.json）仍解析到真实用户；缺省 = 当前账户 profile（现状 Load）。
        var dirIdx = Array.FindIndex(args, a => a.Equals("--config-dir", StringComparison.OrdinalIgnoreCase));
        if (dirIdx >= 0 && dirIdx + 1 < args.Length)
        {
            HomeOverride.Set(args[dirIdx + 1]);
        }

        // 单实例互斥体（与 UI 互斥体互不相干，二者可共存；二次 --headless 静默 exit 0）
        using var mutex = new Mutex(true, DevMode.HeadlessMutexName, out var createdNew);
        if (!createdNew) return 0;

        try
        {
            var cfg = AppConfig.Load(null); // 配置文件本体恒为 exe 同目录（§2.3：不因 --config-dir 改变）
            if (cfg.Relay is not { Enabled: true })
            {
                return 0; // relay 关闭态 / 无 relay 段：直接退出，绝不监听端口占坑挡 UI（E3）
            }

            var warnings = new List<string>();
            var mgr = new RelayManager(cfg, cfg.Relay);
            if (!mgr.Start(warnings))
            {
                CrashLog("中转启动失败：" + string.Join(Environment.NewLine, warnings));
                return 1;
            }

            WorkingSetTrimmer.Trim(); // M7：启动后裁剪一次
            using var trimTimer = new System.Threading.Timer(
                _ => WorkingSetTrimmer.Trim(),
                null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10)); // M7：每 10 分钟（改善观感，非真实省内存）

            GC.KeepAlive(mutex);
            GC.KeepAlive(mgr);
            GC.KeepAlive(trimTimer);
            Thread.Sleep(Timeout.Infinite); // 驻留；Windows 关机直接终止进程（in-flight 丢弃，与断电语义一致）
            return 0;
        }
        catch (Exception ex)
        {
            CrashLog(ex.ToString());
            return 1;
        }
    }

    static void CrashLog(string text)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), CrashLogName),
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + text);
        }
        catch
        {
            /* ignore */
        }
    }
}

/// <summary>
/// 工作集裁剪（M7）：SetProcessWorkingSetSize(-1, -1)。只改善任务管理器观感（页会故障回入、
/// 净内存不变、裁剪瞬间有页错误尖峰），不改变物理驻留；裁剪时点与 A10 采样窗口对齐（锯齿容差）。
/// </summary>
static class WorkingSetTrimmer
{
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    public static void Trim()
    {
        try
        {
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
            /* ignore */
        }
    }
}
