namespace GlmBalanceTaskbar;

/// <summary>
/// CLI 命令注册表（帮助与分派同源）：每个命令一个定义，详细帮助文本由 CliHelp 提供。
/// Name 为完整命令形态（含占位参数，如 "channel add &lt;id&gt;"）；Group/Sub 用于二级分派。
/// </summary>
static class CliRegistry
{
    public sealed record Cmd(
        string Name,                    // "channel add <id>"（展示用）
        string Group,                   // "channel"
        string Sub,                     // "add"
        string Summary,                 // 顶层/分组一行说明
        Func<string[], bool, int> Run)  // (剩余参数, jsonOut) → 退出码
    {
        /// <summary>纯命令路径（不含占位参数，如 "channel add"）。</summary>
        public string Path => Group == Sub ? Group : Group + " " + Sub;
    }

    public static readonly Cmd[] Commands =
    [
        new("status", "status", "status", "中转运行态与渠道状态", CliRunner.CmdStatus),
        new("health", "health", "health", "探活运行实例", CliRunner.CmdHealth),
        new("logs", "logs", "logs", "请求日志查看（--tail N，1–500，默认 50）", CliRunner.CmdLogs),
        new("discover", "discover", "discover", "发现 opencode/Pi 配置中的可用渠道", CliRunner.CmdDiscover),
        new("deploy <agent>", "deploy", "deploy", "部署 localrelay 配置到智能体端（--dry-run 预览 / --set-default 设全局默认）", CliRunner.CmdDeploy),
        new("relay on", "relay", "on", "启用中转（未初始化时自动初始化空配置）", (a, j) => CliRunner.CmdRelayOnOff(a, j, true)),
        new("relay off", "relay", "off", "关闭中转（写回 enabled=false 并停运行时）", (a, j) => CliRunner.CmdRelayOnOff(a, j, false)),
        new("relay show", "relay", "show", "查看中转运行参数", CliRunner.CmdRelayShow),
        new("relay set", "relay", "set", "修改运行参数（--port-set / --probe-interval / --degrade-below / --restore-above / --cooldown-*）", CliRunner.CmdRelaySet),
        new("relay reset-token", "relay", "reset-token", "重置 localToken（需同步重新部署 opencode 端）", CliRunner.CmdRelayResetToken),
        new("channel list", "channel", "list", "列出渠道池（id/优先级/enabled/probe/key 掩码/models）", CliRunner.CmdChannelList),
        new("channel show <id>", "channel", "show", "单渠道详情", CliRunner.CmdChannelShow),
        new("channel add <id>", "channel", "add", "新建渠道", (a, j) => CliRunner.CmdChannelAddSet(a, j, true)),
        new("channel set <id>", "channel", "set", "更新渠道（掩码形态 apiKey = 不修改）", (a, j) => CliRunner.CmdChannelAddSet(a, j, false)),
        new("channel remove <id>", "channel", "remove", "删除渠道（pin 中的渠道连带清 pin）", CliRunner.CmdChannelRemove),
        new("channel enable <id>", "channel", "enable", "启用渠道", (a, j) => CliRunner.CmdChannelEnableDisable(a, j, true)),
        new("channel disable <id>", "channel", "disable", "停用渠道", (a, j) => CliRunner.CmdChannelEnableDisable(a, j, false)),
        new("channel test <id>", "channel", "test", "上游连通性测试", CliRunner.CmdChannelTest),
        new("channel move <id> up|down", "channel", "move", "优先级相邻交换", CliRunner.CmdChannelMove),
        new("channel reorder <id>...", "channel", "reorder", "全量重排优先级（须恰好覆盖全部渠道 id）", CliRunner.CmdChannelReorder),
        new("channel pin <id>", "channel", "pin", "设为强制通道（运行时单槽，重启失效）", (a, j) => CliRunner.CmdChannelPin(a, j, true)),
        new("channel unpin <id>", "channel", "unpin", "解除强制通道（幂等）", (a, j) => CliRunner.CmdChannelPin(a, j, false)),
        new("channel import [id...]", "channel", "import", "从发现列表导入（无参=全部未入池候选）", CliRunner.CmdChannelImport),
        new("shutdown", "shutdown", "shutdown", "停止后台 headless 进程（仅对 --headless 实例生效；UI 承载时 403）", CliRunner.CmdShutdown),
        new("task status", "task", "status", "开机任务注册状态 + 后台服务探活", CliRunner.CmdTaskStatus),
        new("task register", "task", "register", "注册开机计划任务（需管理员）+ 立即拉起 headless", CliRunner.CmdTaskRegister),
        new("task unregister", "task", "unregister", "注销开机计划任务（停 headless → 删除任务）", CliRunner.CmdTaskUnregister),
        new("init <dir>", "init", "init", "初始化 relay 配置段（等价 --relay-init <dir>）", CliRunner.CmdInit),
    ];

    /// <summary>二级分派：group 必配；sub 可空（单层命令）。</summary>
    public static Cmd? Find(string group, string? sub)
    {
        if (sub == null)
        {
            // 单层命令：Group == Sub == 命令名
            return Commands.FirstOrDefault(c => c.Group == group && c.Sub == group);
        }
        return Commands.FirstOrDefault(c => c.Group == group &&
            (c.Sub.Equals(sub, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>按完整路径（"channel add"）查找。</summary>
    public static Cmd? FindByPath(string path)
    {
        var parts = path.Split(' ', 2);
        return parts.Length == 2 ? Find(parts[0], parts[1]) : Find(parts[0], null);
    }

    /// <summary>未知命令的最近建议（前缀/包含匹配；找不回 null）。</summary>
    public static string? Nearest(string group, string? sub)
    {
        var input = sub == null ? group : group + " " + sub;
        var hit = Commands.FirstOrDefault(c => c.Path.StartsWith(input, StringComparison.OrdinalIgnoreCase))
               ?? Commands.FirstOrDefault(c => c.Group == group);
        return hit?.Path;
    }

    /// <summary>全部分组名（去重，按注册顺序）。</summary>
    public static IEnumerable<string> Groups() =>
        Commands.Select(c => c.Group).Distinct();
}
