using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace GlmBalanceTaskbar;

/// <summary>
/// schtasks 计划任务封装 + HKCU Run 键读写（G2 自启动，§2.2/§2.5）。
/// 任务名 GlmBalanceTaskbarRelay（扁平 ASCII 名）；存在性三态 ExistsState：exit 0=Yes、明确
/// 「找不到」=No、其余非零（拒绝访问等）=Locked 按「已注册」推断——存在性检查先于权限检查，
/// 普通权限查 SYSTEM+ONSTART 任务被拒 ≠ 未注册；
/// 「是否在运行」改用 /health 探活判定（AppContext 委托态，§2.4）。
/// 提权子命令入口（--task-register / --task-unregister，管理员上下文）：执行 schtasks 并以
/// MessageBox 报告结果（WinExe 可直接弹窗），随后退出——本文件含 WinForms 引用，但仅被
/// UI 进程与提权子进程消费；headless 路径（HeadlessRunner）与 Main 分派不触达（R1-F3）。
/// </summary>
static class ScheduledTask
{
    public const string TaskName = "GlmBalanceTaskbarRelay";
    const string RunKeyName = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "GlmBalanceTaskbar";

    static readonly string ExePath =
        Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];

    // ─────────────────────────── schtasks 封装 ───────────────────────────

    // schtasks 控制台输出用 OEM 代码页（中文系统 936、英文系统 437）。不显式指定时 .NET 按控制台
    // 默认编码解码，中文「找不到」因编码失配成乱码、白名单永远匹配不上 → 全部误判 Locked。
    // CodePagesEncodingProvider 随 WindowsDesktop 框架自带（WinForms 依赖链），零 NuGet。
    static readonly Encoding SchtasksOutputEncoding = CreateSchtasksOutputEncoding();

    static Encoding CreateSchtasksOutputEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
    }

    static (int Code, string Output) RunSchtasks(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = SchtasksOutputEncoding,
                StandardErrorEncoding = SchtasksOutputEncoding,
            });
            if (p == null) return (1, "schtasks 启动失败");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, (stderr.Trim().Length > 0 ? stderr : stdout).Trim());
        }
        catch (Exception ex)
        {
            return (1, ex.Message);
        }
    }

    /// <summary>任务存在性三态：Yes=确定存在；No=确定不存在；Locked=无法确认（按已注册推断）。</summary>
    public enum TaskExistsState { Yes, No, Locked }

    /// <summary>
    /// 任务存在性三态（schtasks /Query）：Yes=exit 0；No=非零且输出匹配「找不到」白名单（中英，
    /// 不区分大小写——实测：查询不存在的任务返回「错误: 系统找不到指定的文件」/"cannot find"，
    /// 与「拒绝访问」错误形态可区分）；Locked=其余一切非零（拒绝访问等）——SYSTEM+ONSTART 任务
    /// 默认安全描述符拒普通用户查询，存在性按「已注册」推断：宁可信其存在，不可误报未注册。
    /// </summary>
    public static TaskExistsState ExistsState() =>
        ClassifyExists(RunSchtasks($"/Query /TN \"{TaskName}\""));

    /// <summary>判定核心：仅 exit 0 与明确「找不到」可下结论，其余非零一律 Locked（白名单严格）。</summary>
    static TaskExistsState ClassifyExists((int Code, string Output) result) =>
        result.Code == 0 ? TaskExistsState.Yes
        : OutputSaysNotFound(result.Output) ? TaskExistsState.No
        : TaskExistsState.Locked;

    /// <summary>输出是否明确「任务不存在」（中英白名单，不区分大小写；仅此形态可判 No）。</summary>
    static bool OutputSaysNotFound(string output) =>
        output.Contains("找不到") ||
        output.Contains("cannot find", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// /Create /F 注册开机 SYSTEM 任务（/SC ONSTART /RU SYSTEM）。
    /// /TR 转义（§5 S2）：整值一层引号，内部路径引号以 \" 逃逸——路径含空格必须双层保住。
    /// 命令行形如：schtasks /Create /F /TN GlmBalanceTaskbarRelay /SC ONSTART /RU SYSTEM
    ///   /TR "\"&lt;exe&gt;\" --headless --config-dir \"&lt;profile&gt;\""
    /// </summary>
    public static bool Register(string profileDir, out string error)
    {
        var tr = $"/TR \"\\\"{ExePath}\\\" --headless --config-dir \\\"{profileDir}\\\"\"";
        var (code, output) = RunSchtasks(
            $"/Create /F /TN \"{TaskName}\" /SC ONSTART /RU SYSTEM {tr}");
        error = output;
        return code == 0;
    }

    /// <summary>手动组装的注册命令行（回退提示/弹窗展示用，供管理员终端手动执行）。</summary>
    public static string ManualRegisterCommand(string profileDir) =>
        $"schtasks /Create /F /TN \"{TaskName}\" /SC ONSTART /RU SYSTEM " +
        $"/TR \"\\\"{ExePath}\\\" --headless --config-dir \\\"{profileDir}\\\"\"";

    /// <summary>/Run 立即拉起（注册流内，R1-F7：管理员上下文允许对 SYSTEM 任务 /Run，注册即生效）。</summary>
    public static bool RunTask() => RunSchtasks($"/Run /TN \"{TaskName}\"").Code == 0;

    /// <summary>/End 停任务引擎启动的运行中实例（任务粒度终止；不误杀同镜像名的 UI 实例，§2.5）。</summary>
    public static bool EndTask() => RunSchtasks($"/End /TN \"{TaskName}\"").Code == 0;

    // ─────────────────────────── HKCU Run（登录时启动副开关） ───────────────────────────

    /// <summary>HKCU Run 键是否已写（副开关勾选态）。读取失败返回 false（勾选态按未注册呈现）。</summary>
    public static bool RunAtLogonEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyName);
            return key?.GetValue(RunValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>写/删 HKCU Run 键（值名 GlmBalanceTaskbar，值 = "exe路径"）。失败返回 false（零提权路径）。</summary>
    public static bool SetRunAtLogon(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyName);
            if (enable) key.SetValue(RunValueName, "\"" + ExePath + "\"");
            else key.DeleteValue(RunValueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ─────────────────────────── 提权子命令入口（管理员上下文） ───────────────────────────

    /// <summary>
    /// --task-register：/Create 成功后 /Run 立即拉起（R1-F7）；/Run 失败不阻塞注册结果
    /// （弹窗注明「将在下次开机生效」）。结果经 MessageBox 报告后退出。
    /// <paramref name="args"/> 中的 --config-dir 由父 UI 进程显式传入（A-F5：over-the-shoulder
    /// UAC 下提权子进程的 UserProfile ≠ 交互用户 profile）；缺省退回当前进程 profile（手工诊断）。
    /// 退出码：0 = 注册成功（含 /Run 失败分支），1 = /Create 失败。
    /// </summary>
    public static int RunRegisterCommand(string[] args)
    {
        var dirIdx = Array.FindIndex(args, a => a.Equals("--config-dir", StringComparison.OrdinalIgnoreCase));
        var profile = dirIdx >= 0 && dirIdx + 1 < args.Length && args[dirIdx + 1].Length > 0
            ? args[dirIdx + 1]
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var ok = Register(profile, out var error);
        if (!ok)
        {
            MessageBox.Show(
                "开机后台服务注册失败：" + (error.Length > 0 ? error : "schtasks 返回非零退出码") +
                "\n\n可复制以下命令到管理员终端手动执行：\n" + ManualRegisterCommand(profile),
                "注册开机服务", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
        var runOk = RunTask();
        MessageBox.Show(
            runOk
                ? $"开机后台服务已注册并启动。\n\n任务名：{TaskName}\n中转已改由后台服务承载（下次开机自动运行，无需登录）。"
                : $"开机后台服务已注册，但立即启动未成功（将在下次开机生效）。\n\n任务名：{TaskName}",
            "注册开机服务", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    /// <summary>
    /// --task-unregister：按序 /End（停任务实例）→ 探活 /health（仍存活则 /api/shutdown 兜底，
    /// Bearer token 从 exe 同目录配置读取，§2.5）→ /Delete /F，弹窗报告。
    /// /Delete 失败按三态复查（C-F3 幂等）：输出明确「找不到」= 任务本就不存在 → 幂等成功；
    /// /Query 复查 Yes = 任务仍在 → 报错；No = 已不存在 → 幂等成功（注明「任务不存在，视为未注册」）；
    /// Locked = 无法确认 → 不得判「仍存在」失败，按注销成功处理并加提示。
    /// 退出码：0 = 注销完成（含幂等清理——任务已删/不存在，下次开机不再运行），1 = /Delete 失败且任务仍在。
    /// </summary>
    public static int RunUnregisterCommand()
    {
        var notes = new List<string>();
        EndTask();          // 失败不阻塞（任务可能未在运行）
        ShutdownRunningHeadless(notes); // /api/shutdown 兜底（手工启动的诊断用 headless 不受 /End 管辖）
        var (code, output) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        var goneNote = "";
        if (code != 0)
        {
            // C-F3：/Delete 失败 ≠ 任务仍在——输出明确「找不到」直接判不存在（幂等清理，与设计
            // §7 R7 一致）；否则按 /Query 三态复查（Locked 时不得判「仍存在」失败）
            var state = OutputSaysNotFound(output) ? TaskExistsState.No : ExistsState();
            if (state == TaskExistsState.Yes)
            {
                MessageBox.Show(
                    "注销失败：" + (output.Length > 0 ? output : "schtasks 返回非零退出码") +
                    $"\n\n可手动执行：schtasks /Delete /TN \"{TaskName}\" /F",
                    "注销开机服务", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
            if (state == TaskExistsState.No)
            {
                goneNote = "\n\n任务不存在，视为未注册。";
            }
            else
            {
                notes.Add($"任务状态查询受限（需管理员权限），无法确认删除结果；" +
                          $"若下次开机仍自启，请以管理员执行：schtasks /Delete /TN \"{TaskName}\" /F");
            }
        }
        var tail = notes.Count > 0 ? "\n\n" + string.Join("\n", notes) : "";
        MessageBox.Show(
            $"开机后台服务已注销：计划任务已删除，运行中的后台进程已停止。{goneNote}{tail}",
            "注销开机服务", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    /// <summary>探活仍存活 → POST /api/shutdown 兜底（token 属本地秘密文件，仅本进程读取，不新增暴露面）。</summary>
    static void ShutdownRunningHeadless(List<string> notes)
    {
        try
        {
            var cfg = AppConfig.Load(null);
            if (cfg.Relay is not { } relay) return;
            var client = new DelegatedRelayClient(relay.Port);
            if (client.ProbeOnce() == null) return; // 已失联（/End 已生效），无需兜底
            var stopped = client.RequestShutdown(relay.LocalToken).GetAwaiter().GetResult();
            notes.Add(stopped
                ? "已通过 /api/shutdown 停止运行中的后台进程。"
                : "后台进程停止请求未确认（token 可能不匹配）；若进程仍驻留，可在任务管理器手动结束。");
        }
        catch (Exception ex)
        {
            notes.Add("后台进程兜底停止失败：" + ex.Message);
        }
    }
}
