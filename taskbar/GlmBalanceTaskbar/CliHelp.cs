namespace GlmBalanceTaskbar;

/// <summary>
/// CLI 帮助体系（三层：总览 / 分组 / 子命令）。详细帮助文本按命令 Path 存表；
/// 分组帮助由注册表按 Group 聚合生成，保证帮助与命令注册同源不漂移。
/// 帮助始终输出人读文本（不支持 --json），打印到 stdout。
/// </summary>
static class CliHelp
{
    const string Exe = "GlmBalanceTaskbar.exe";

    /// <summary>命令详细帮助（Usage 行 / Options 块 / Details / Examples；无 Options 的命令省略该节）。</summary>
    static readonly Dictionary<string, (string Usage, string Options, string Details, string Examples)> Details =
        new(StringComparer.Ordinal)
    {
        ["status"] = (
            $"{Exe} status",
            "",
            "输出中转运行态（running/enabled/port/当前渠道/今日请求与转移次数）与渠道状态表：\n" +
            "  状态列 state = normal | cooldown（硬冷却，带剩余秒） | degraded（软降级） | disabled | no-key；\n" +
            "  REMAIN = 探测剩余 %（较小窗口值）；ID 带 * = sticky pin 强制通道；ERR = 最近错误（截断）。",
            $"{Exe} status\n{Exe} status --json"),

        ["health"] = (
            $"{Exe} health",
            "",
            "对运行实例做 /health 探活。实例不可达 → 退出码 2；用于脚本快速判断中转是否在位。",
            $"{Exe} health"),

        ["logs"] = (
            $"{Exe} logs [--tail <n>]",
            "  --tail <n>        最近 N 条（1–500，默认 50）",
            "查看最近请求日志（时间/模型/渠道/转移链/状态码/耗时/错误）。日志不含请求体内容。\n" +
            "环形缓冲上限 500 条。",
            $"{Exe} logs\n{Exe} logs --tail 100\n{Exe} logs --json"),

        ["discover"] = (
            $"{Exe} discover",
            "",
            "从 opencode / Pi 配置发现可用渠道（id/名称/key 掩码/models/建议探测类型/是否已在池）。\n" +
            "与 admin 面板发现列表同源；发现到的 id 可用于 channel import。",
            $"{Exe} discover\n{Exe} discover --json"),

        ["deploy"] = (
            $"{Exe} deploy <agent> [--dry-run] [--set-default]",
            "  --dry-run         只生成预览，不写盘（dev 实例恒为 dry-run）\n" +
            "  --set-default     同时把 localrelay 写为 opencode 全局默认模型",
            "把中转连接信息部署到智能体配置（opencode：写 ~/.config/opencode/opencode.json(.jsonc) 的\n" +
            "localrelay provider，自动备份 *.bak-<时间戳>，绝不改动既有 provider 条目）。\n" +
            "agent 取值：opencode | pi。",
            $"{Exe} deploy opencode --dry-run\n{Exe} deploy opencode --set-default"),

        ["relay on"] = (
            $"{Exe} relay on",
            "",
            "启用中转（等价 admin 设置 Tab「启用」开关）：未运行即启动；未初始化时自动初始化空配置。\n" +
            "UI（托盘）实例与 headless 实例皆可。",
            $"{Exe} relay on"),

        ["relay off"] = (
            $"{Exe} relay off",
            "",
            "关闭中转：写回 enabled=false 并停止运行时（先写回再停机，保证响应可达）。\n" +
            "不影响实例进程本身；headless 进程停止后驻留退出由其自身语义决定。",
            $"{Exe} relay off"),

        ["relay show"] = (
            $"{Exe} relay show",
            "",
            "查看中转运行参数（端口/探测间隔/降级阈值/冷却/流式超时/鉴权形态/admin 地址）。",
            $"{Exe} relay show\n{Exe} relay show --json"),

        ["relay set"] = (
            $"{Exe} relay set <选项>...",
            "  --port-set <n>                      监听端口（1–65535；变更需重启程序生效）\n" +
            "  --probe-interval <n>                探测间隔秒（10–3600）\n" +
            "  --degrade-below <n>                 剩余 % 低于此值软降级（0–100）\n" +
            "  --restore-above <n>                 剩余 % 高于此值恢复（0–100，滞后防抖）\n" +
            "  --cooldown-quota <n>                上游 429/402/403 冷却秒（5–86400）\n" +
            "  --cooldown-error-threshold <n>      5xx/网络异常连续 N 次进冷却（1–100）\n" +
            "  --cooldown-error-seconds <n>        连续错误冷却时长秒（5–86400）",
            "修改中转运行参数并即时生效（端口除外）。未提供的选项保持现值；范围外取值由服务端钳制。\n" +
            "端口选项名是 --port-set（--port 已被全局「目标端口覆盖」占用）。",
            $"{Exe} relay set --degrade-below 8 --restore-above 12\n{Exe} relay set --cooldown-quota 600"),

        ["relay reset-token"] = (
            $"{Exe} relay reset-token",
            "",
            "重置 localToken 并写回配置。输出新 token（仅此一次显示）。\n" +
            "重置后 opencode 端 localrelay provider 必须同步重新部署（deploy opencode），否则请求 401。",
            $"{Exe} relay reset-token"),

        ["channel list"] = (
            $"{Exe} channel list",
            "",
            "列出渠道池（按优先级升序）：PRI / ID / ENABLED / PROBE / KEY（掩码）/ MODELS / NAME。",
            $"{Exe} channel list\n{Exe} channel list --json"),

        ["channel show <id>"] = (
            $"{Exe} channel show <id>",
            "",
            "单渠道详情（含探测要素 organization/project/usageType 与 key 掩码）。",
            $"{Exe} channel show zhipu-coding"),

        ["channel add <id>"] = (
            $"{Exe} channel add <id> [选项]",
            "  --name <s>          显示名（缺省 = id）\n" +
            "  --baseURL <url>     上游 OpenAI 兼容基地址（建议提供；缺省渠道不参与路由）\n" +
            "  --apiKey <key>      渠道 API key（缺省 = 复用全局读取链；第三方渠道必须显式填写，\n" +
            "                      否则判为 no-key 不参与路由）\n" +
            "  --models <a,b,c>    模型列表（逗号分隔）\n" +
            "  --priority <n>      优先级（缺省 = 当前最大 + 1）\n" +
            "  --probe <t>         探测类型：zhipu-monitor | none（缺省 none）\n" +
            "  --organization <id> 团队版组织 ID（探测要素；空串 = 清除回落全局）\n" +
            "  --project <id>      团队版项目 ID（探测要素；空串 = 清除回落全局）\n" +
            "  --usageType <1|2>   用量维度：1=个人 2=团队（缺省自动）\n" +
            "  --enabled <bool>    是否启用（true/false，缺省 true）",
            "新建渠道并写回配置文件（PersistAndApply），即时生效；非冷却渠道立即重新参与路由。\n" +
            "id 已存在时等价于 channel set（服务端 PUT 为新建或更新语义）。",
            $"{Exe} channel add my-relay --baseURL https://x.example.com/v1 --apiKey sk-xxx --models glm-5.3,glm-5.3-flash"),

        ["channel set <id>"] = (
            $"{Exe} channel set <id> [选项]",
            "  （选项同 channel add，全部可选；未提供的字段保持现值）",
            "更新渠道（PATCH 语义：只修改提供的选项）。apiKey 掩码形态（sk-…后4位）视为「未修改」保留原值。\n" +
            "配置修正后非冷却渠道立即重新评估参与路由。",
            $"{Exe} channel set my-relay --models glm-5.3 --enabled true"),

        ["channel remove <id>"] = (
            $"{Exe} channel remove <id>",
            "",
            "删除渠道并写回生效；删除被 pin 渠道时自动清除强制通道。",
            $"{Exe} channel remove my-relay"),

        ["channel enable <id>"] = (
            $"{Exe} channel enable <id>",
            "",
            "启用渠道（enabled=true，写回并生效）。",
            $"{Exe} channel enable custom-relay"),

        ["channel disable <id>"] = (
            $"{Exe} channel disable <id>",
            "",
            "停用渠道（enabled=false，写回并生效；不参与路由但保留配置）。",
            $"{Exe} channel disable custom-relay"),

        ["channel test <id>"] = (
            $"{Exe} channel test <id>",
            "",
            "对渠道上游做连通性测试（真实请求探测）。测试失败 → 退出码 1（服务端返回 ok=false）。",
            $"{Exe} channel test zhipu-coding"),

        ["channel move <id> up|down"] = (
            $"{Exe} channel move <id> <up|down>",
            "",
            "优先级相邻交换（按 Priority 升序与相邻渠道互换）。已在边界时提示无需移动并返回成功。",
            $"{Exe} channel move custom-relay up"),

        ["channel reorder <id>..."] = (
            $"{Exe} channel reorder <id1> <id2> ...",
            "",
            "全量重排优先级并持久化（Priority 重编号 1..n）。ids 必须恰好包含全部渠道 id\n" +
            "（无重复、无缺失），否则服务端 400。",
            $"{Exe} channel reorder custom-relay zhipu-coding"),

        ["channel pin <id>"] = (
            $"{Exe} channel pin <id>",
            "",
            "设为强制通道（sticky pin，运行时单槽，不写配置、重启失效）：流量固定路由到该渠道，\n" +
            "渠道不可路由（冷却/降级/停用）时自动让位既有优先级规则，恢复后 pin 自动重新生效。",
            $"{Exe} channel pin zhipu-coding"),

        ["channel unpin <id>"] = (
            $"{Exe} channel unpin <id>",
            "",
            "解除强制通道（幂等；pin 当前指向其他渠道时不清除）。",
            $"{Exe} channel unpin zhipu-coding"),

        ["channel import [id...]"] = (
            $"{Exe} channel import [id1] [id2] ...",
            "",
            "从发现列表导入渠道（id 形态：agent:id，如 opencode:zhipu）。无参 = 导入全部未入池候选。\n" +
            "已入池或未发现的 id 记入 skipped；导入即写回生效。",
            $"{Exe} channel import\n{Exe} channel import pi:zhipu-coding"),

        ["shutdown"] = (
            $"{Exe} shutdown",
            "",
            "请求运行实例停止并退出。仅对 --headless 后台进程生效：\n" +
            "  · UI（托盘）实例承载中转 → 服务端 403 拒绝（终止的是整个托盘程序，不允许远程触发）；\n" +
            "  · 免鉴权故障态（localToken 为空）或 Host 头不符 → 403。\n" +
            "200 = 已提交，进程即将退出。",
            $"{Exe} shutdown"),

        ["task status"] = (
            $"{Exe} task status",
            "",
            "查看开机计划任务（GlmBalanceTaskbarRelay）注册状态、后台服务探活结果与「登录时启动」开关。\n" +
            "任务存在性三态：exit 0=已注册；明确「找不到」=未注册；查询受限（SYSTEM 任务拒普通用户）=按已注册推断。",
            $"{Exe} task status\n{Exe} task status --json"),

        ["task register"] = (
            $"{Exe} task register [--config-dir <profile>]",
            "  --config-dir <p>   用户 profile 根（over-the-shoulder UAC 下显式传入；缺省 = 当前进程 profile）",
            "注册开机计划任务（schtasks /Create /F /SC ONSTART /RU SYSTEM，需要管理员权限）并立即拉起 headless。\n" +
            "dev 实例禁止执行（--dev 与本命令组合报错退出）。",
            $"{Exe} task register"),

        ["task unregister"] = (
            $"{Exe} task unregister",
            "",
            "注销开机计划任务：停任务实例 → /api/shutdown 兜底（手工启动的 headless 亦覆盖）→ /Delete /F。\n" +
            "幂等：任务不存在时视为未注册成功。dev 实例禁止执行。",
            $"{Exe} task unregister"),

        ["init <dir>"] = (
            $"{Exe} init <dir>",
            "",
            "对 <dir>\\glmbalance-taskbar.json 执行与 UI「启用中转」开关完全相同的初始化例程\n" +
            "（生成 relay 段、播种渠道、自动生成 localToken；不监听端口，可在 live 实例运行时安全执行）。\n" +
            "等价于既有参数 --relay-init &lt;dir&gt;。dev（--dev init）遵守 dev 目录守卫。",
            $"{Exe} init C:\\Users\\me\\relay-config"),
    };

    static readonly Dictionary<string, string> GroupDescription = new(StringComparer.Ordinal)
    {
        ["relay"] = "中转启停与参数",
        ["channel"] = "渠道池管理（增删改查 / 排序 / 强制通道）",
        ["task"] = "开机自启计划任务",
    };

    static readonly Dictionary<string, string[]> GroupOrder = new(StringComparer.Ordinal)
    {
        ["relay"] = ["on", "off", "show", "set", "reset-token"],
        ["channel"] = ["list", "show", "add", "set", "remove", "enable", "disable", "test", "move", "reorder", "pin", "unpin", "import"],
        ["task"] = ["status", "register", "unregister"],
    };

    /// <summary>顶层总览（help / --help / -h / help 无参）。</summary>
    public static void PrintOverview()
    {
        Console.WriteLine(
            $"""
             {Exe} CLI —— GLM 中转与配额命令行控制

             用法：
               {Exe} <命令> [参数]
               {Exe} help <分组|命令>        查看分组或命令的详细帮助
               <命令> --help                 同上（命令内帮助入口）

             全局选项（可加在任意位置）：
               --json            以 JSON 输出（脚本/管道消费）
               --dev             操作开发实例（dev 配置目录、dev 默认端口 8788）
               --port <n>        覆盖目标端口（缺省取配置 relay.port）
               --config-dir <p>  指定用户 profile 根（key 链解析，同 --headless 语义）

             命令：
            """);
        foreach (var c in CliRegistry.Commands)
        {
            Console.WriteLine($"  {c.Name,-32} {c.Summary}");
        }
        Console.WriteLine(
            $"""

             进程级参数（与子命令并列，保持兼容）：
               --once            抓取一次配额并打印（含渠道评估）
               --relay-init <dir> 初始化 relay 配置段（= init <dir>）
               --headless        以后台进程承载中转（开机任务入口；--config-dir 同上）
               --task-register / --task-unregister   开机任务注册/注销（= task register/unregister）
               --dump-icon       图标渲染诊断
               --dev             开发实例（与 --headless/--task-* 组合被硬禁）

             示例：
               {Exe} status
               {Exe} channel list --json
               {Exe} channel pin zhipu-coding
               {Exe} relay off
               {Exe} help channel add

             退出码：0=成功  1=失败（含业务错误）  2=实例不可达  3=用法错误
             """);
    }

    /// <summary>分派帮助入口：help [topic...]；topic 可为分组（channel/relay/task）或命令（status / channel add）。</summary>
    public static void Print(string[] topic)
    {
        if (topic.Length == 0) { PrintOverview(); return; }

        // 组名（有多个子命令的组）→ 分组帮助
        var g = topic[0];
        if (topic.Length == 1 && GroupOrder.ContainsKey(g))
        {
            PrintGroup(g);
            return;
        }
        // 命令路径（1–2 token；第二 token 可能是子命令）
        var path = topic.Length >= 2 ? g + " " + topic[1] : g;
        var entry = CliRegistry.FindByPath(path);
        if (entry != null)
        {
            Console.WriteLine(RenderDetail(entry));
            return;
        }
        if (CliRegistry.FindByPath(g) is { } single)
        {
            Console.WriteLine(RenderDetail(single));
            return;
        }
        Console.WriteLine($"error: 未知帮助主题 \"{string.Join(" ", topic)}\"");
        Console.WriteLine($"运行 {Exe} help 查看全部命令");
    }

    /// <summary>分组帮助：该组全部子命令列表。</summary>
    static void PrintGroup(string group)
    {
        var cmds = CliRegistry.Commands.Where(c => c.Group == group).ToList();
        Console.WriteLine($"{group} —— {GroupDescription.GetValueOrDefault(group, group)}");
        Console.WriteLine();
        Console.WriteLine("子命令：");
        foreach (var c in cmds)
        {
            Console.WriteLine($"  {c.Name,-32} {c.Summary}");
        }
        Console.WriteLine();
        Console.WriteLine($"详见：{Exe} help {group} <子命令>");
    }

    /// <summary>按展示名（含占位符）查找详细帮助；Path（无占位符）兜底。</summary>
    static (string Usage, string Options, string Details, string Examples) DetailOf(CliRegistry.Cmd entry) =>
        Details.TryGetValue(entry.Name, out var d) ? d
        : Details.TryGetValue(entry.Path, out d) ? d
        : ($"{Exe} {entry.Name}", "", entry.Summary, $"{Exe} {entry.Name}");

    /// <summary>用法错误时打印的简短用法行（TryDispatch 消费）。</summary>
    public static string RenderUsage(CliRegistry.Cmd entry) =>
        "用法：\n  " + DetailOf(entry).Usage;

    /// <summary>命令详细帮助（help <cmd> / <cmd> --help / 用法错误就地展示）。</summary>
    public static string RenderDetail(CliRegistry.Cmd entry)
    {
        var d = DetailOf(entry);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(d.Usage.Length > 0 ? d.Usage : $"{Exe} {entry.Name}");
        sb.AppendLine();
        if (d.Options.Length > 0)
        {
            sb.AppendLine("选项：");
            sb.AppendLine(d.Options);
            sb.AppendLine();
        }
        sb.AppendLine("说明：");
        sb.AppendLine(d.Details);
        sb.AppendLine();
        sb.AppendLine("示例：");
        sb.AppendLine(d.Examples);
        sb.AppendLine();
        sb.AppendLine("退出码：0=成功  1=失败  2=实例不可达  3=用法错误");
        return sb.ToString();
    }
}
