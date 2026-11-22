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

        // 零 WinForms 哨兵（A-F3）：运行时自检——若 Main 分派或本路径被后续改动引入了
        // WinForms 可达类型引用，程序集已被 JIT 类型解析加载，此处必然查到并写崩溃日志报警。
        // 纯查询 GetAssemblies 不触发加载；PublishSingleFile 下 Modules 锚点弱化（托管程序集
        // 从 bundle 内存加载），此哨兵跨发布形态可复现，替代注释纪律成为结构性守护。
        SentinelCheckLoadedWinForms();

        try
        {
            // 单实例互斥体（与 UI 互斥体互不相干，二者可共存；二次 --headless 静默 exit 0）。
            // 在 try 块内创建（A-F9）：构造异常（ACL/会话边界）也走崩溃日志，不留无日志退出。
            using var mutex = new Mutex(true, DevMode.HeadlessMutexName, out var createdNew);
            if (!createdNew) return 0;

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

    /// <summary>
    /// 零 WinForms 前提的运行时哨兵（A-F3，R1-F3/A2 的进程内守护）：发现
    /// System.Windows.Forms 程序集已被加载即写崩溃日志报警（功能不受影响，仅 M1 内存基线
    /// 收益落空——治本需回查 Main 分派与 headless 路径的类型图）。查询失败不影响主流程。
    /// </summary>
    static void SentinelCheckLoadedWinForms()
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName().Name;
                if (name != null && name.StartsWith("System.Windows.Forms", StringComparison.Ordinal))
                {
                    CrashLog("[sentinel] headless 进程检测到 WinForms 程序集已加载：" + name +
                             "。零 WinForms 前提被破坏（Main 分派或 headless 路径新增了可达的 WinForms 类型引用），" +
                             "M1 内存收益已落空；relay 功能不受影响，请走查 Program.Main 与 HeadlessRunner 的类型图。");
                    return;
                }
            }
        }
        catch
        {
            /* 自检失败不影响主流程 */
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
