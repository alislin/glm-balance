# 设计文档 v2：中转 headless 化（开机未登录运行）+ 内存瘦身（headless-relay-lightweight）

- 状态：设计 v2（planner 修订轮 1/3 产出，落实评审 R1 全部 findings，待 design-reviewer 复审）
- 编排：`glm-balance-headless-relay` t16；背景见编排目录 `00 背景.md`；前版 v1 保留于 `headless-relay-lightweight.md`（不覆盖，评审对照用）
- 范围：仅本设计文档；不改源码/配置/README；零 NuGet 依赖；`PublishSingleFile`、`--dev` 隔离、单文件单 exe 承诺不变
- 结构与 v1 一致（七问 + 涉及模块 + 边界场景 + 内存清单 + 风险红线 + 验收标准），修订处以（R1-Fx）标注来源

## 修订记录（R1-F1 ~ F10 逐条处置）

| # | 级别 | 处置 | 落实内容与 v2 落点 |
|---|---|---|---|
| R1-F1 | major | 已落实 | GC 键改为 `System.GC.ConserveMemory`（取值 **0–9、默认 0**，.NET 6+ 生效；官方文档建议试值 5–7），§2.6 L3 行 / §4 构建侧 csproj 行 / §6 M6 三处统一，取 5（实现期在 3/5/9 实测定值）；「两项均实测验证」修正为「键名与取值范围经 dotnet/runtime（gcconfig.h）与官方 GC 配置文档核实，收益数值为实现期实测项」。DATAS 备选已评估：.NET 9+ **默认启用**，官方 runtimeconfig 键为 `System.GC.DynamicAdaptationMode`（评审提示的 `System.GC.DATAS` 经查官方文档非 runtimeconfig 键名，按文档修正）；本项目 net10.0 下 DATAS 已默认生效，A/B 设计见 §2.6 |
| R1-F2 | major | 已落实 | §2.4 表「管理面板入口」行改为「`OpenAdminPanel` 委托态分流（小改）」：绕过本地 Running 守卫（AppContext.cs L245–259 `_relay is not { Running: true }`），从配置 relay.port 推导 `http://127.0.0.1:<port>/admin` 直接打开；覆盖详情窗按钮 / 托盘菜单 / 缺 key 引导按钮 `_btnRelayGuide` 三处同入口；§4 AppContext 行同步改写（消除 v1 表格「零改动」与 §4「列改动」互相矛盾）；A5 增三入口验证点 |
| R1-F3 | major | 已落实 | §4 Program.cs 行显式化为两条动作：① 新增 `RunHeadless`/`RunTaskRegister`/`RunTaskUnregister` 静态方法与早退分派；② **现有 L61–129 UI 启动块整体抽入 `RunUi` 静态方法，Main 收缩为纯参数分派**；§2.1 补 JIT 全方法体类型解析依据；新增评审/验收锚点「headless 进程模块列表不含 System.Windows.Forms.dll」（并入 A2） |
| R1-F4 | minor | 已落实 | §3 数据流不变量、§5 E5 措辞精确化：承认 S4 窗口内写意图重叠（token 生成/播种写回先于端口绑定），但重叠期可达成的写入均为单字段合并；全节点写（token 生成/播种）在 S4 前提下不可达，最坏互覆盖仍为 enabled 布尔；§7 R4 同步引用 |
| R1-F5 | minor | 已落实 | §5 S3 补「开机早期 W32Time 未同步 → 出站 TLS 证书时间校验失败，同步后自愈」已知限制；§7 并入 R2 行（与 E6 代理限制症状同形） |
| R1-F6 | minor | 已落实 | `/api/shutdown` 单点加固（§2.5 停止路径、§4 AdminApi 行、§7 R11/红线）：`EffectiveToken` 为空时 403 拒绝 + Host 头必须等于 `127.0.0.1:<port>`，消除免鉴权故障态 + 回环 DNS rebinding 的最坏分支 |
| R1-F7 | minor | 已落实（评审二选一，取「实现 /Run」） | 提权子进程 `/Create` 成功后执行 `schtasks /Run /TN GlmBalanceTaskbarRelay` 立即拉起 headless，注册即进入委托态；`/Run` 失败不阻塞注册结果，弹窗注明「将在下次开机生效」（§2.2、§2.5 状态行、S1、A3） |
| R1-F8 | minor | 已落实 | 委托态回退本地 Start 前先 `AppConfig.Load` 重载并以新配置重建 `RelayManager`（§5 S5、§4 AppContext 行）；A16 验证回退后含 admin 面板新增渠道 |
| R1-F9 | minor | 已落实 | §8 新增 A15（S4 竞态收敛）/ A16（S5 回退 + 重载配置）/ A17（G4 key 轮换跟随）；评审建议两条之外，把其问题陈述①点名的 S5 回退路径一并补上测试锚点 |
| R1-F10 | minor | 已落实 | A10 明确计数器与判定式：计数器取 `Get-Process` 的 `WorkingSet64`（PowerShell 原生属性，无性能计数器本地化问题；「工作集」口径较「专用工作集」系统性偏高 10–20MB 共享页，阈值由 60MB 放宽至 80MB 并给出换算依据）；判定式 = 10 次采样线性拟合斜率 ≤ 0 且末 3 次均值 ≤ 首次 + 10MB；M7 裁剪时点与采样窗口对齐说明写入 M7/A10 |

十条 findings 全部落实，无否决项。评审「事实抽查记录」表中已核实的引用（Probe 回退、home 三处、Program.cs 行号、IconRenderer/DetailForm 行号、SetIcon Dispose、Persist/WriteAtomic、掩码、互斥体名、默认代理等）直接沿用，未复查。

## 结论摘要

- 七问结论：① 同 exe 增 `--headless` 分支（不拆项目），**Main 收缩为纯参数分派、现有 UI 启动块整体抽入 `RunUi`**（headless 进程不加载 WinForms 的结构性前提，R1-F3）；② `schtasks /SC ONSTART /RU SYSTEM` + 自体 `runas` 提权子命令（注册成功后 `/Run` 立即拉起，R1-F7），UAC 拒绝回退 HKCU Run；③ key 走 `--config-dir <用户profile>`（home 重定向，跟随 auth.json 轮换），不冻结；④ UI 启动探活 `/health`+`/status`，委托态只读渲染、不写配置，**管理面板三入口经 `OpenAdminPanel` 委托态分流打开远端面板**（R1-F2）；⑤ 详情窗新增「自启动」分组（含可选「登录时启动」HKCU Run 副开关），dev 全隐藏且硬禁注册；⑥ 内存：headless 分离（结构性主收益）+ IconRenderer 渲染减半与 ICO 字节缓存 + **GC `ConserveMemory` 调优（v1 键名 `System.GC.Conserve` 不存在，已修正，R1-F1）** + 可选工作集裁剪；⑦ Session 0 下 HttpListener 可用（http.sys 内核、回环绑定已有生产先例），双写竞争经「委托态 UI 零写盘 + headless 禁用即退出」消除。

## 1. 目标 / 非目标

### 目标

- G1 headless：中转以独立进程在**登录前**（开机）运行，承载 RelayServer + 探测 + AdminApi；不做 UI 配额轮询、不建托盘/窗口；**headless 进程模块列表不含 System.Windows.Forms.dll（可验证的结构性约束，R1-F3，见 §2.1）**。
- G2 自启动开关：详情窗可注册/注销「开机自启动（无需登录）」计划任务；注册成功即拉起 headless 进入委托态（`/Run`，R1-F7）；状态可检测；dev 实例全部隐藏且硬禁注册。
- G3 委托模式：UI 启动/运行中探活既有 headless 实例，委托态下 relay 状态只读展示，UI 不自起 relay、不写配置文件；管理面板三入口可打开远端面板（R1-F2）。
- G4 key 来源：SYSTEM 账户下 headless 仍能解析智谱 key，且**跟随 auth.json 轮换**（不冻结，验收 A17）。
- G5 内存：headless 进程基线显著低于现 UI 进程；登录后 UI 进程也降低 GDI/GC 开销与观感内存。
- G6 生产兼容：不带新参数时行为与现状逐字节一致；`--once`/`--dump-icon`/`--relay-init`/`--dev` 语义不变。

### 非目标

- 不做 Windows 服务（ServiceBase/sc.exe）包装；不做 Windows 通知区外的任何 headless UI。
- headless 不做托盘/配额展示（登录后由 UI 承担）；不做 headless 专属日志系统（仅崩溃日志）。
- v1 委托态 UI 不代理 admin 写 API（pin/启停经 headless 的 /admin 面板完成；见 §2.4 取舍）。
- 不重构 RelayManager/AdminApi 的既有职责边界；不引入任何 NuGet 包；不改 dev-prod 分离既有语义。
- 不处理 headless 的优雅停机信号（Windows 关机直接终止进程；in-flight 请求丢弃，与今天断电语义一致，见 §7）。

## 2. 方案选项与取舍

### 2.1 Q1 进程模型：同 exe `--headless` 分支 vs 独立项目

| 选项 | 说明 | 优点 | 缺点 | 结论 |
|---|---|---|---|---|
| A 同 exe 增 `--headless` 分支 | `Program.Main` 收缩为**纯参数分派**（仅 `DevMode.Initialize` + 分支跳转）：① 在 `ApplicationConfiguration.Initialize()`（L61）之前插入 `--headless` / `--task-register` / `--task-unregister` 早退分支，与既有 `--once`/`--dump-icon`/`--relay-init`（L45–59）同范式，headless 逻辑放独立静态方法（如 `RunHeadless`）；② **现有 L61–129 UI 启动块（Initialize、互斥体、AppContext 构造、watcher 线程、Application.Run、异常兜底）整体抽入新静态方法 `RunUi`**（R1-F3） | 单文件单 exe 发布承诺不变；RelayManager/RelayServer/AdminApi/Config/DevMode 全量复用，零代码分叉；发布管线一条；headless 进程**不加载 WinForms/GDI 程序集**（前提 = Main 的 IL 无 WinForms 可达引用，见下方 JIT 依据） | exe 体积对 headless 而言多背了 WinForms 托管载荷（框架依赖发布约 220KB，可忽略）；纪律面两条：headless 分支方法不引用 WinForms 类型，**且 Main 本体经 `RunUi` 抽取后同样不含任何 WinForms 可达引用**（R1-F3） | **推荐** |
| B 拆独立项目（如 `GlmBalanceTaskbar.RelayHost`） | relay 运行时抽到共享项目，两个 exe | headless exe 无 WinForms 依赖，理论上基线更小 | 破坏「仍单文件单 exe」承诺（用户需分发两个文件）；csproj 结构改造 + 共享代码工程化；RelayManager 构造依赖 `AppConfig`/`DevMode` 派生逻辑，拆分牵连面大；双发布、双版本漂移风险 | 否决（成本远超收益；收益仅剩基线几 MB，§6 另有裁剪手段） |

**JIT 依据与验收锚点（R1-F3）**：

CoreCLR 按方法粒度 JIT，编译 Main 时会解析方法体内**全部类型引用**（含基类链解析）——类型加载发生在编译期，与运行时分支走向无关。现有 Main L61–129 含 `new AppContext(cfg)`（L104，AppContext 基类为 ApplicationContext）与 `Application.Run(ctx)`（L117）：即使 headless 分支在 L61 之前早退、这些语句永不执行，只要 IL 留在 Main 里，类型解析就会加载 System.Windows.Forms.dll（类型加载需解析基类链），M1 的 60–100MB 基线收益**静默落空**。故「只加早退分支」不够，必须把 UI 启动块整体搬出 Main。验收锚点：**headless 进程模块列表不含 `System.Windows.Forms.dll`**（`(Get-Process -Id <pid>).Modules` / Process Explorer；并入 §8 A2）。

**A 的职责边界（headless = 什么做、什么不做）**：

- 做：`AppConfig.Load`（含 §2.3 home 重定向）→ `RelayManager.Start`（token/播种写回、建池、`RelayServer.Start`、探测循环）→ `AdminApi` 全端点 → 崩溃日志。
- 不做：30s 配额轮询（AppContext/GlmApiClient 用量主循环）、`NotifyIcon`/`DetailForm`/`TrayPreviewForm`、任务栏进度条、AUMID 声明、`System.Windows.Forms.Timer`。
- headless 下 relay 若 `enabled=false` 或无 `relay` 段：**直接退出**（exit 0），绝不监听端口占坑（否则会挡住 UI 的 relay，见 §5 边界 E3）。
- 探测循环（`RelayManager.ProbeLoopAsync`）属于 relay 职责保留；`ZhipuMonitorProbe` 已确认具备自主回退取数能力（Probes/ZhipuMonitorProbe.cs L26–48：快照缺失/过期 → 用 provider 显式 key 或 `Global.ApiKey` 自行 `GlmApiClient.FetchUsageAsync`），headless 无 AppContext 快照不影响其工作，**唯一前提是 key 可解析（§2.3）**。

### 2.2 Q2 启动方式：计划任务 + UAC 提权交互与回退

**启动方式选项对比**：

| 选项 | 说明 | 优点 | 缺点 | 结论 |
|---|---|---|---|---|
| A 计划任务 `schtasks /SC ONSTART /RU SYSTEM` | schtasks.exe 子进程（零 NuGet）注册开机 SYSTEM 任务 | 真正「未登录运行」；schtasks 是系统自带命令，`/SC ONSTART` 值非本地化；任务粒度 `/End` 可精准停本任务进程（不会误杀同名 exe 的 UI 实例） | 创建 SYSTEM 任务需管理员提权（UAC）；SYSTEM 下有 key 链与代理限制（§2.3/§7） | **推荐** |
| B Windows 服务（sc.exe + ServiceBase） | 改造 exe 增服务入口 | 服务控制管理器管理，自动重启策略 | WinExe 体系外重构（ServiceBase 专用生命周期）；同样需提权安装；与「单 exe 双形态」耦合更深；开关交互比 schtasks 繁琐 | 否决 |
| C HKCU Run（登录启动） | 注册表 Run 键 | 零提权、实现最简 | 登录后才启动，不满足「未登录也能转发」的核心诉求 | 降级回退方案（§2.5 副开关） |
| D shell:startup 快捷方式（现状 README 方式） | 用户手工放启动文件夹 | 无代码 | 同 C，且无状态检测/注销能力 | 保留兼容说明，不作为本功能 |

**任务定义（注册命令，实际转义见 §5 场景 S2）**：

```text
任务名：GlmBalanceTaskbarRelay（扁平名，避免 schtasks 文件夹不自动创建的问题；ASCII，无本地化问题）
schtasks /Create /F
  /TN GlmBalanceTaskbarRelay
  /SC ONSTART /RU SYSTEM
  /TR "\"<exe 绝对路径>\" --headless --config-dir \"C:\Users\<用户名>\""
立即启动：schtasks /Run /TN GlmBalanceTaskbarRelay
  （注册成功后由提权子进程顺手执行，R1-F7；管理员上下文允许对 SYSTEM 任务 /Run，注册即生效无需等重启）
注销：schtasks /End /TN GlmBalanceTaskbarRelay（停运行中实例）→ schtasks /Delete /TN GlmBalanceTaskbarRelay /F
状态检测：schtasks /Query /TN GlmBalanceTaskbarRelay，仅取退出码（0=存在，1=不存在）；
  不解析输出文本（本地化不可靠）；「是否在运行」改用 /health 探活判定（§2.4）。
exe 路径取 `Environment.ProcessPath`（单文件发布下的真实 exe 路径）。
```

**提权交互方案（普通权限 UI → 创建 SYSTEM 任务必须 UAC）**：

采用**自体提权子命令**（复用同一 exe，避免直接提权 schtasks.exe 时的控制台黑窗闪烁）：

1. UI 按钮 → `Process.Start(new ProcessStartInfo(exe) { Verb = "runas", UseShellExecute = true, Arguments = "--task-register" })` 触发 UAC 同意框。
2. 提权子进程（同一 exe，管理员身份）执行 `schtasks /Create …`；成功后**顺手执行 `schtasks /Run /TN GlmBalanceTaskbarRelay` 立即拉起 headless**（R1-F7：闭环「注册了怎么没生效」的困惑——`/Run` 失败不阻塞注册结果，弹窗文案注明「将在下次开机生效」）；捕获输出与退出码，**用 MessageBox 报告结果**（提权进程是 WinExe，可直接弹窗），随后退出。
3. 父进程对子进程 `WaitForExit` 后重新 `schtasks /Query`（普通权限查询 SYSTEM 任务定义通常允许）刷新开关状态；headless 拉起后 UI 的委托探活（30s 节流）自然接管，状态行转为「已注册 · 后台服务运行中」；若查询失败，状态行显示「已提交注册，结果见提权弹窗」，不阻塞。
4. 注销同理：`--task-unregister` 子进程按序 `/End` → 探活 `/health`（仍存活则走 §2.4 的 `/api/shutdown` 兜底）→ `/Delete /F`，弹窗报告。
5. 捕获 `Win32Exception` 原生错误码 1223（用户取消 UAC）→ 不创建任何任务，进入回退提示。

**失败回退**：

- UAC 取消 / 提权失败（非管理员组成员等）→ MessageBox 给出两选项：**降级注册「登录时启动」**（HKCU Run，零提权，只起 UI；relay 由 UI 进程承载，语义回到现状）或 **手动指引**（提示用管理员终端执行注册命令，文档附命令原文）。
- `schtasks /Create` 失败（组策略禁用 schtasks 等）→ 弹窗展示完整命令行供手动执行；UI 状态保持未注册。
- 回退动作不产生半注册状态：提权子进程先注册成功再弹窗；父进程状态一律以 `/Query` 退出码为准。

**headless 进程自身（与注册解耦，手工诊断也用）**：

- 单实例互斥体：新增 `DevMode` 派生属性 `HeadlessMutexName`（`Local\GlmBalanceTaskbar.Headless`；与 UI 互斥体 `Local\GlmBalanceTaskbar.SingleInstance` 互不相干，二者可共存）。二次 `--headless` 启动 → 静默 exit 0。
- 崩溃日志：沿用 AppContext 模式写 `%TEMP%\glmbalance-headless-crash.log`（SYSTEM 下为 C:\Windows\Temp，可写）。
- 主线程 `Thread.Sleep(Timeout.Infinite)` 驻留；无优雅停机钩子（见 §1 非目标）。
- **dev 硬禁**：`--dev --headless`、`--dev --task-register/--task-unregister` 一律报错退出（exit ≠ 0），与「dev 禁止注册系统任务」的用户确认一致。

### 2.3 Q3 key 来源：profile 传参 vs 注册时冻结 key

**问题事实**：SYSTEM 账户下 `Environment.GetFolderPath(UserProfile)` 解析到 `C:\WINDOWS\system32\config\systemprofile`（Config.cs L35 的 `home`），`~/.local/share/opencode/auth.json → ~/.config/opencode/{opencode,tui}.json` 全链落空；exe 同目录配置文件本身 SYSTEM 可读，provider 显式 key 不受影响，只有「空 key 回落全局读取链」的渠道（默认播种的 zhipu-coding）变 `no-key`。

| 选项 | 说明 | 优点 | 缺点 | 结论 |
|---|---|---|---|---|
| A `--config-dir <用户profile>`（home 重定向传参） | headless 启动参数携带用户 profile 根；进程内设单一静态点（如 `HomeOverride.Path`），`AppConfig.Load`（Config.cs L35）、`OpenCodeConfigSource`（L15）、`OpenCodeDeployer`（L21）三处 `GetFolderPath(UserProfile)` 统一改经该点取值（null = 现状） | key 跟随 auth.json **轮换不冻结**（token 过期/换 key 重启 headless 即生效）；不新增明文 key 落盘点；SYSTEM 默认对用户 profile 有读取权；实现收敛为单一派生点（对齐 DevMode 派生属性模式） | 依赖用户目录 ACL（EFS 加密/显式 Deny 时失败，见 §7）；`/TR` 命令行变长 | **推荐** |
| B 注册时把解析出的 key 冻结写进配置 | UI 注册时读取全局链解析 key，明文写入 exe 同目录配置的 `apiKey` 字段 | 不依赖用户目录 ACL；headless 无参数 | auth.json 轮换后 headless **静默持有过期 key**（401 转移、探测失败，难排查）；明文 key 出现第二落盘点（违背 key 最小暴露精神）；注册时 UI 需「读 key → 写 key」，扩大明文触达面 | 否决 |

**推荐方案语义细化（A）**：

- 参数沿用契约命名 `--config-dir <dir>`，语义 = **用户 profile 根重定向**（key 链与发现/部署的 home）；不改变配置文件本体位置——`glmbalance-taskbar.json` 仍为 exe 同目录**单一文件**（UI 与 headless 共读共享，避免双配置文件双写者，这是 §2.4 单写者原则的前提）。
- UI 可从自身进程推导：注册命令中 `--config-dir` 取 `Environment.GetFolderPath(UserProfile)`，无需用户输入。
- `--config-dir` 缺省时退回当前账户 profile（手工诊断场景），行为 = 现状 Load。
- **红线核查**：明文 key 不进 git 仓库内任何文件——本设计零新增 key 落盘点（`--config-dir` 值是路径非 key）；key 只写不读不变（headless 复用同一 AdminApi 掩码逻辑，`/status`/日志掩码路径逐字节一致）。
- key 不可解析时的可见性：`/status` 渠道 `HasKey=false` → 委托态 UI 复用**既有引导行**（DetailForm.UpdateRelay 的「请在 Provider 池填写 API Key」分支）自然呈现，无新增 UI 状态。轮换跟随性验收见 A17（R1-F9）。

### 2.4 Q4 委托模式：探活、只读展示、单写者

**探活协议**：

- UI 启动时（`AppContext` 构造前，仅当 `cfg.Relay is { Enabled: true }`）：`GET http://127.0.0.1:<relay.port>/health`（超时 400ms，避免拖慢启动）→ 200 则 `GET /status` 解析为 `RelayStatus`（新增 `RelayStatusJson.TryParse`，与 `RelayJson.Serialize` 同形互逆）→ 进入**委托态**。
- 运行中：委托态随既有 30s 刷新节流一并重取 `/status`；非委托态在每次 relay 相关操作失败（如本地 Start 因端口被占失败）后补探活一次，成功即切换委托态（覆盖启动竞态）。
- **指纹策略**：不引入强指纹——凡在配置端口上 `/health` 通过且 `/status` 可解析的实例，按定义就是「该端口的中转服务」，委托它总是正确的。可选增强：`RelayStatus` 增 `Headless: bool` 字段（RelayManager 构造时由模式注入，/status 形状多一字段、旧字段不变），仅用于委托态提示文案区分「由开机后台服务运行」vs「由另一实例运行」。dev 携带既有 `dev:true`，委托提示同样可区分。

**委托态 UI 表现（DetailForm / AppContext 改动点）**：

| UI 元素 | 委托态行为 | 实现落点 |
|---|---|---|
| relay 渠道行 | 正常渲染（`_form.UpdateRelay(远端 RelayStatus)`，渲染代码零改动） | AppContext 刷新路径分流 |
| 「启用/关闭中转」开关 | 禁用 + `_lblRelayHint` 显示「中转由开机后台服务运行（只读）；启停请用管理面板或注销自启动」 | `SetDelegated(bool)` 新方法 |
| 渠道行右键 pin 菜单 | 禁用（pin 是运行时单槽意图，属 headless 进程内存态，UI 无 token 不应代理写） | `ShowRelayRowMenu` 判委托态直接返回 |
| 管理面板入口（详情窗「管理面板」按钮 / 托盘菜单项 / 缺 key 引导按钮 `_btnRelayGuide`，三处共用 `PanelRequested` 同一入口） | 照常打开 `http://127.0.0.1:<port>/admin`——URL 由同一配置文件推导，指向 headless，全功能管理天然可用。**v1「零改动」表述错误（R1-F2）：现有 `OpenAdminPanel` 硬守卫本地 `_relay is not { Running: true }`（AppContext.cs L245–259）会弹「中转未启用，请先在详情窗口启用中转」后返回，委托态三处入口全部失效** | **`OpenAdminPanel` 委托态分流（小改，R1-F2）**：绕过本地 Running 守卫，从配置 relay.port 推导远端 URL 直接打开（详见 §4 AppContext 行） |
| 托盘悬停中转行 | 用远端 `Snapshot` 的 `PreviewLine()` 等价信息（简化：仅渠道数与 current，或直接省略该行） | 可选 |

| 选项 | 说明 | 优点 | 缺点 | 结论 |
|---|---|---|---|---|
| i 只读委托（HTTP 读） | 如上表 | UI 无 token 依赖、无 401 流程；改动面最小；单写者天然成立 | UI 内不能 pin/启停（管理面板可） | **采用** |
| ii UI 代理写 API | UI 从配置文件读 localToken，经 `/api/*` 代理 pin/启停 | UI 功能对齐本地态 | UI 需处理 token/401；启停 headless relay 会使委托态在「运行/关闭」间翻转，状态机复杂化 | 未来增强，本轮不做 |
| iii UI 登录后接管（杀 headless 自起） | UI 启动即停 headless、本地起 relay | 无委托态复杂度 | 违背需求本意（headless 目标是不间断）；频繁交接抖动 | 否决 |

**单写者原则**：委托态 UI 的本地 `RelayManager` 保持构造但 `Start()` 被跳过、`SetEnabled` 不可达（开关禁用）→ UI 全程零配置写盘；唯一写者 = headless 进程（其 `RelayOptions.Persist` 是既有唯一写路径）。非委托态写路径与现状一致。委托态回退本地 Start（S5）前须重载配置再重建 `RelayManager`（R1-F8，见 §5 S5）。

### 2.5 Q5 UI 开关组：详情窗「自启动」分组

**分组设计**（详情窗新增「自启动」区块，位于「模型中转」区块之后；`DevMode.IsDev` 时整个区块不创建）：

| 元素 | 形态 | 语义 |
|---|---|---|
| 状态行（Label） | 「未注册」/「已注册（开机后台服务）」/「已注册 · 后台服务运行中」 | 注册态 = `schtasks /Query` 退出码；「运行中」= 委托探活结果（复用 §2.4，零额外开销）；注册成功经 `/Run` 立即拉起后即转「运行中」（R1-F7，无长期「已注册未运行」窗口） |
| 主开关（Button） | 未注册：「注册开机服务（无需登录）」；已注册：「注销开机服务」 | 注册/注销走 §2.2 提权子命令流；注册含 `/Run` 立即拉起；注销含运行中 headless 停止（`/End` + `/api/shutdown` 兜底） |
| 副开关（Button/CheckBox） | 「登录时启动（仅界面）」勾选态 = HKCU Run 键存在 | 独立于主开关；零提权；只起 UI 不起 headless |
| 说明行（Label，可选） | 灰字一句话：「开机服务独立于本窗口运行；登录后本程序仅显示用量」 | 消除「关掉 UI 服务还在跑」的困惑 |

**「是否同时提供登录时启动（HKCU Run）」取舍**：

| 选项 | 论证 | 结论 |
|---|---|---|
| 提供（默认关） | ① UAC 被拒时的现成降级路径（§2.2 回退直接落到它）；② 只想要「登录后看用量」、不需要未登录转发的用户覆盖面；③ 与 headless 组合无冲突（委托态下 UI 自启只起界面，不重复起 relay） | **推荐提供** |
| 不提供 | 少一个开关，界面更简 | 用户只能手工放 shell:startup，回到无状态检测的旧世界；UAC 拒绝后无落点 |

- HKCU Run 键：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 值名 `GlmBalanceTaskbar`，值 `"exe路径"`（不带参数）。读写经 `Microsoft.Win32.Registry`（Windows 桌面运行时内置；若实现期发现链接问题，回退 `reg.exe` 子进程，行为验收不变）。
- 与主开关正交：可只开副开关（登录起 UI，relay 走 UI 进程，现状语义）、只开主开关（开机起 headless，UI 手动开）、或都开（开机 headless 承载 relay，登录 UI 起来后自动进入委托态）。

**headless 运行中的停止路径（注销生命周期）**：`schtasks /End /TN GlmBalanceTaskbarRelay` 是任务粒度终止，**不能**用 `taskkill /IM GlmBalanceTaskbar.exe`（会误杀同镜像名的 UI 实例）。`/End` 只杀任务引擎启动的实例；手工启动的诊断用 headless 不受其管辖 → 注销流程在 `/End` 后探活 `/health`，仍存活则 `POST /api/shutdown`（新增端点，Bearer localToken 鉴权，提权子进程从 exe 同目录配置读 token；token 属本地秘密文件，不新增暴露面）→ headless `RelayManager.Stop()` 后 `Environment.Exit(0)`。**端点单点加固（R1-F6）**：`/api/shutdown` 在 `EffectiveToken` 为空（免鉴权本身是故障态）时直接 403 拒绝，且校验 Host 头必须等于 `127.0.0.1:<port>`——简单 POST 无自定义头、无 CORS 预检，免鉴权态下回环 DNS rebinding 页面即可触发进程终止，一行实现成本消除最坏分支。

**状态检测时机**：`schtasks /Query` 是子进程调用（数十毫秒），不做 30s 常驻轮询——仅在详情窗打开、执行注册/注销后、以及托盘菜单打开详情前刷新缓存；「运行中」态复用委托探活（常驻 30s 节流，免费）。

### 2.6 Q6 内存瘦身策略

策略分四层，逐项收益/代价清单见 §6，此处只做方向取舍：

| 层 | 选项对比 | 结论 |
|---|---|---|
| L1 结构性：relay 移出 WinForms 进程 | (a) headless 分离（§2.1 方案 A）——常驻路径不再加载 WinForms/GDI，砍掉 60–100MB 基线的主因（前提 = Main 抽取纪律，R1-F3）；(b) 仅 UI 进程内部调优——上限受限，WinForms 运行时基线无法消除 | **(a) 主收益，(b) 兜底**（登录后 UI 进程仍需调优，故有 L2–L4） |
| L2 GDI churn：IconRenderer | (a) 渲染去重（每尺寸渲染一次，10→5 次）+ ICO 字节缓存（数值/颜色不变时 0 次渲染）；(b) 只调轮询间隔/降低刷新频率——治标，30s 一次的 churn 依旧累积 | **(a)**，含 DetailForm 共享字体（每刷新 4+ 次 `new Font` → 静态复用） |
| L3 GC 运行时调优 | (a) csproj `RuntimeHostConfigurationOption`：`System.GC.ConserveMemory=5`（**取值 0–9、默认 0，.NET 6+ 生效**；官方文档建议试值 5–7，实现期在 3/5/9 实测定值）+ `System.GC.Concurrent=false`（关后台 GC，低分配应用暂停无感）；(b) `System.GC.HeapHardLimitPercent` 硬顶——SSE 长流/256MB 请求体场景有 OOM 风险 | **(a)**；键名与取值范围经 dotnet/runtime（gcconfig.h）与官方 GC 配置文档核实——v1 所写 `System.GC.Conserve` 键**不存在**，照抄会被运行时静默忽略、M6 收益虚置（R1-F1）；收益为实现期实测项，若有时延回归即回退 |
| L4 工作集裁剪 | (a) `SetProcessWorkingSetSize(-1,-1)` 定时裁剪（headless 启动后 + 每 10 分钟；UI 最小化时 + 每 10 分钟）；(b) 不裁剪——任务管理器观感差，用户已明确表达内存抱怨 | **(a) 默认启用**，明确标注取舍：只改善观感（页会故障回入、净内存不变、裁剪瞬间有页错误尖峰），不改变实际驻留 |

**DATAS A/B 备选（R1-F1 评估项）**：DATAS 自 **.NET 9 起默认启用**（官方 runtimeconfig 键 `System.GC.DynamicAdaptationMode`、MSBuild 属性 `GarbageCollectionAdaptationMode`；评审提示中写的 `System.GC.DATAS` 键名经查官方 GC 配置文档不存在，按文档修正）。本项目 net10.0 下 DATAS 已默认生效，且其 gen0 预算公式在未显式指定 conserve 设置时内部默认按 5 计——「省内存倾向」已部分内建。实现期 A/B 三组（过程用临时 runtimeconfig 变体对比、不入库；验收统一走 A10/A11 口径）：**B0** 现状基线（DATAS 默认开、不设 ConserveMemory）；**B1** = B0 + `ConserveMemory=5`（csproj 默认落盘配置）；**B2** = B0 关 DATAS（`DynamicAdaptationMode=0`）+ `ConserveMemory=5`。取工作集/时延综合最优组合落盘；csproj 最终只保留选定项。

原则：所有项零 NuGet、零发布产物形态变化（`PublishSingleFile` 不变）；每项独立可回退（配置项/常量开关）；GC 键名/取值一律以官方文档为准（v1 教训：键名错误 → 配置被运行时静默忽略、收益虚置，实现与验收均不可省）；验收见 §8 A10/A11。

## 3. 推荐方案总览

```
开机（未登录）                登录后
─────────────                ─────────────
[schtasks ONSTART, SYSTEM]    [用户双击/自启 UI]
GlmBalanceTaskbar.exe         GlmBalanceTaskbar.exe（UI）
  --headless                    ├ 30s 配额轮询 → 图标/托盘/详情窗（现状不变）
  --config-dir C:\Users\<u>     ├ 探活 /health → 委托态：/status 只读渲染
  ├ HeadlessMutex 单实例        ├ 零配置写盘（单写者 = headless）
  ├ RelayServer(8787 回环)      ├ 管理面板三入口 → OpenAdminPanel 委托态分流（R1-F2）
  ├ 探测循环（自主取数）        └ 自启动分组：注册（含 /Run 立即拉起）/注销/HKCU Run
  ├ AdminApi（/admin /api/*）   opencode ──▶ 127.0.0.1:8787 ──▶ headless
  └ 工作集定时裁剪                     （UI 退出不影响）
```

**headless 启动序列**：`Main` 纯参数分派（§2.1，`RunUi` 已抽取、Main 的 IL 无 WinForms 可达引用）→ 命中 `--headless` 进入 `RunHeadless`（在 `ApplicationConfiguration.Initialize()` 之前分派，WinForms 不初始化也不可达）→ dev 拒绝 → HeadlessMutex 抢占（失败 exit 0）→ `AppConfig.Load(configDir: exe 同目录[不变], homeOverride: --config-dir)` → `relay` 段缺失或 `enabled=false` → exit 0（不占端口）→ `RelayManager(cfg, opts).Start(warnings)`（失败：崩溃日志 + exit 1）→ 工作集裁剪一次 → 驻留。

**模式注入**：`DevMode` 增加 `IsHeadless` 派生属性（`--headless` 时 true），`RelayManager.Snapshot()` 的 `RelayStatus` 增 `Headless` 字段（默认 false，旧消费者零感知）；UI 侧委托态提示据此措辞。

**数据流不变量**：headless 与 UI 用的是**同一个** exe 同目录 `glmbalance-taskbar.json`、同一个 relay.port；token 生成/播种/配置写回全部经 headless（或非委托态下的 UI）既有 `RelayManager.Persist` 单路径。写竞争面（§5 E5，R1-F4 精确化）：token 生成/播种写回发生在端口绑定之前，S4 竞态窗口内**写意图存在重叠**；但 S4 前提（relay enabled 且可注册任务）必然意味着 localToken/providers 已持久化，两进程的 token 生成与播种分支均不可达，**重叠期可达成的写入全部是单字段合并（enabled 布尔）**，最坏互覆盖仍为 enabled 布尔、下一操作自愈；真正的全节点写冲突（双 token 分叉）需「enabled=true 且无 localToken」配置态，与 S4 前提矛盾、实际不可达。

## 4. 涉及模块与接口（文件级改动清单）

运行时/入口侧：

| 文件 | 改动 | 要点 |
|---|---|---|
| `Program.cs` | **两条动作（R1-F3 显式化）**：① 新增 `RunHeadless` / `RunTaskRegister` / `RunTaskUnregister` 静态方法与早退分派（在 L61 `ApplicationConfiguration.Initialize()` 之前，与既有 `--once`/`--dump-icon`/`--relay-init` L45–59 同范式）；② **现有 L61–129 UI 启动块（Initialize、互斥体、AppContext 构造、watcher 线程、Application.Run、异常兜底）整体抽入新静态方法 `RunUi`，Main 收缩为纯参数分派**（仅 DevMode.Initialize + 分支跳转），使 Main 的 IL 不含任何类型图可达 WinForms 的引用 | JIT 全方法体类型解析依据见 §2.1（只加早退分支不够，UI 块不搬出则 headless 照样加载 WinForms、M1 收益静默落空）；dev 硬禁在分支最前；`RunRelayInit` 既有范式复用（AttachParentConsole 可选）；**评审/验收锚点：headless 进程模块列表不含 System.Windows.Forms.dll（A2）** |
| `HeadlessRunner.cs`（新增） | headless 组合根 | HeadlessMutex、home 重定向注入、Start 失败崩溃日志、`Thread.Sleep(Timeout.Infinite)` 驻留、裁剪计时器；不引用任何 WinForms 类型 |
| `ScheduledTask.cs`（新增） | schtasks 封装 + HKCU Run | 任务名常量 `GlmBalanceTaskbarRelay`；`Register/Unregister/Exists`（仅退出码）；`/Run` 立即拉起（注册流内，R1-F7）；`/End`+`/api/shutdown` 兜底序；Run 键读写（Registry API，失败回退 reg.exe）；提权子命令入口（MessageBox 报结果） |
| `DelegatedRelayClient.cs`（新增） | UI 侧探活/拉取 | `/health`（400ms 超时）+ `/status`（1.5s 超时）；回环 HttpClient 静态复用 |
| `DevMode.cs` | 增 `IsHeadless`、`HeadlessMutexName` 派生属性 | 对齐既有派生模式；dev 下 headless 相关派生值不被消费（入口已硬禁） |
| `Config.cs` | `Load` 增 home 重定向（可选参数或经 `HomeOverride` 静态点） | L35 `home` 取值改经单一重定向点，null = 逐字节现状；`auth.json/opencode.json/tui.json` 三处路径随之 |
| `Relay/Discovery/OpenCodeConfigSource.cs`、`Relay/Deploy/OpenCodeDeployer.cs` | home 取值同上重定向 | 否则 SYSTEM 下发现/部署会落到 systemprofile（L15/L21 两处） |

UI 侧：

| 文件 | 改动 | 要点 |
|---|---|---|
| `AppContext.cs` | 委托探活分流 + **`OpenAdminPanel` 委托态分支（R1-F2）** + 自启动事件接线 + 裁剪计时器 | 构造时 probe → 委托态则跳过 `relay.Start`，刷新路径改拉 `/status` 喂 `_form.UpdateRelay`；`OpenAdminPanel` 增委托态分支：绕过本地 `_relay is not { Running: true }` 守卫（L245–259），从配置 relay.port 推导 `http://127.0.0.1:<port>/admin` 直接打开——详情窗按钮 / 托盘菜单 / `_btnRelayGuide` 引导按钮三入口共用此分流；新增 `AutostartRequested` 等事件；**S5 回退动作前先 `AppConfig.Load` 重载并以新配置重建 `RelayManager` 再 Start（R1-F8：委托态构造期 `_opts` 是过时快照，UI 零写盘故重载零副作用）** |
| `DetailForm.cs` | 「自启动」区块（dev 不创建）+ `SetDelegated` + 共享字体 | 开关三态渲染；委托态禁用 relay 开关与 pin 菜单；`new Font(...)` 热点改静态共享字体（不可 Dispose） |
| `Relay/RelayStatus.cs` | `RelayStatus` 增 `Headless: bool = false` 可选字段 | `/status` 形状向后兼容（多一字段）；`RelayManager.Snapshot` 注入 |
| `Relay/RelayJson.cs`（或同层） | 增 `RelayStatusJson.TryParse` | 与 Serialize 同形互逆；委托态 UI 解析用；未知字段忽略 |
| `Relay/AdminApi.cs` + `Relay/RelayServer.cs` | 增 `POST /api/shutdown`（Bearer 鉴权） | `RelayManager.Stop()` → `Environment.Exit(0)`；仅注销流程兜底使用；掩码与鉴权语义同既有 /api/*；**端点单点加固（R1-F6）：`EffectiveToken` 为空时直接 403 拒绝 + Host 头必须等于 `127.0.0.1:<port>`**；响应不含任何敏感字段 |

构建侧：

| 文件 | 改动 | 要点 |
|---|---|---|
| `GlmBalanceTaskbar.csproj` | `ItemGroup` 增 `RuntimeHostConfigurationOption`（GC 调优） | **`System.GC.ConserveMemory=5`（取值 0–9、默认 0；键名经官方文档核实，v1 键 `System.GC.Conserve` 不存在，R1-F1；定值经 §2.6 A/B）**、`System.GC.Concurrent=false`；零 NuGet、`PublishSingleFile` 不变 |

接口约定（新增面）：CLI `--headless [--config-dir <profile>]`、`--task-register`、`--task-unregister`（提权子命令，静默参数，MessageBox 报告；`/Create` 成功后 `/Run` 立即拉起，R1-F7）；HTTP `POST /api/shutdown`（Bearer；空 token 403 + Host 头校验，R1-F6）；`/status` 增 `headless` 字段；HKCU Run 值名 `GlmBalanceTaskbar`；任务名 `GlmBalanceTaskbarRelay`。README/使用文档更新属开发轮次（本轮只交设计）。

## 5. 交互与边界场景

**交互流**：

- S1 首次注册：详情窗点「注册开机服务」→ 确认框（说明 UAC 弹窗将出现）→ runas 自体子命令 → UAC 同意 → 子进程 `/Create` 成功 → **`/Run` 立即拉起 headless（R1-F7；`/Run` 失败不阻塞，文案注明「将在下次开机生效」）** → MessageBox「已注册：后台服务已启动，中转改由其承载」→ 父进程 `/Query` 刷新为已注册，委托探活自然接管状态行。UAC 取消 → 回退对话框（降级 HKCU Run / 手动指引），零残留。
- S2 `/TR` 转义：`/TR "\"C:\bin\local-relay\GlmBalanceTaskbar.exe\" --headless --config-dir \"C:\Users\Alis\""`——整值一层引号，内部路径引号以 `\"` 逃逸；路径含空格必须双层保住；组装后先 `/Query` 校验存在再报告成功。
- S3 开机冷启动：网络未就绪时 headless 监听回环不受影响（http.sys 绑定不依赖外网）；探测/上游请求失败走既有错误计数与重试，网络就绪后自愈；opencode 在此窗口期拿到 502 汇总属预期。**开机早期时钟未同步（R1-F5）**：ONSTART 任务在 W32Time 完成同步前运行，时钟偏差会使出站 TLS（上游 chat/completions、监控 API）证书时间校验失败 → 探测与转发全失败，症状与 E6 代理限制同形、易混淆排查；W32Time 同步后探测循环自愈（与网络未就绪自愈同构）。已知限制，文档标注。
- S4 启动竞态：UI 启动探活恰逢 headless 正在启动（/health 未就绪）→ UI 本地 `Start` 因端口占用失败 → 失败分支补探活（§2.4）→ 下一刷新周期切入委托态；期间详情窗显示既有「启动失败（端口占用）」警告，不静默。收敛性验收见 A15。
- S5 委托态日常：UI 每 30s 拉 `/status` 渲染；用户在 `/admin` 面板增删渠道/改配置 → headless 写盘并热更池 → UI 下轮 `/status` 反映；UI 关闭再开仍委托；headless 重启窗口期 UI 一次性探活失败 → 保持上次快照 + 状态行提示，连续失败 N 次后回退「未委托」：**先 `AppConfig.Load` 重载并以新配置重建 `RelayManager` 再本地 Start（R1-F8——委托期间 admin 面板对 providers/port/参数的变更不在构造期 `_opts` 中，不重载会用过时池启动；UI 本来零写盘，重载零副作用）**，行为对齐现状。回退验收见 A16。

**边界场景**：

- E1 用户目录不可读（EFS 加密 / Deny ACL / 域策略）：headless key 链落空 → zhipu 渠道 `no-key` → `/status` HasKey=false → 委托态 UI 复用既有引导行提示；custom-relay 显式 key 渠道不受影响。文档标注为已知限制。
- E2 端口被第三方占用：headless `Start` 失败 → 崩溃日志 + exit 1；任务仍注册（下次开机重试）；UI 探活失败照现状报错。
- E3 headless 在 relay 关闭态被拉起：直接 exit 0 不监听（绝不占端口挡 UI）；UI 探活失败 → 走本地 relay 现状逻辑。
- E4 exe 移动/升级后任务 `/TR` 指向旧路径：开机任务启动失败（任务计划程序日志可见）；设计取舍：不做 `/Query /V` 文本解析比对（本地化脆弱），注册按钮随时可用 `/F` 覆盖重注册，详情窗状态行在「已注册但探活失败」持续 5 分钟时提示「建议重新注册」。
- E5 双写竞争（R1-F4 措辞精确化）：写意图存在重叠窗口——token 生成/播种写回发生在端口绑定**之前**，S4 竞态内 UI 与 headless 可同时持有写意图；**但重叠期可达成的写入均为单字段合并**：S4 前提（enabled 且可注册任务）下 localToken/providers 必已持久化，两进程的 token 生成与播种分支不可达；全节点写冲突（双 token 分叉：败者以不在文件中的 token 服务）需「enabled=true 且无 localToken」配置态，与 S4 前提矛盾、实际不可达。故最坏互覆盖仍是 enabled 布尔：两者写盘均经 `RelayOptions.Persist` 原子写（WriteAtomic）+ 单字段合并写，下一操作自愈；不引入文件锁（复杂度不值）。
- E6 SYSTEM 无用户代理：`HttpClient` 默认代理在 SYSTEM 下取机器级配置，用户级代理（如本地代理软件的系统代理开关）不可见 → 代理强依赖网络下 headless 出站失败（探测报错 + 请求转移 502），症状与 S3 时钟未同步同形（R1-F5 提示一并排查）。已知限制，文档标注；缓解建议：`HTTPS_PROXY` 环境变量（机器级）或退回「登录时启动」语义。

## 6. 内存优化清单（逐项：预期收益与代价）

| # | 优化项 | 落点 | 预期收益（估） | 代价 / 取舍 |
|---|---|---|---|---|
| M1 | headless 分离（结构主收益） | §2.1/§4 新文件 | 常驻路径不加载 WinForms/GDI/消息泵；relay-only 基线估 35–60MB 私有工作集（编排者预调查：现 UI 内嵌基线 60–100MB 属 WinForms 运行时）；**前提 = `RunUi` 抽取到位（R1-F3），验收锚点 A2 模块列表检查** | 需守护 Main 与 headless 分支均不触碰 WinForms 类型（代码评审锚点） |
| M2 | IconRenderer 渲染去重：每尺寸渲染一次存列表，头/身共用 | `IconRenderer.RenderTextIcon` L57–73 双循环 | 每次图标更新 10 次 `RenderPng` → 5 次；GDI 对象分配减半 | 无可见代价 |
| M3 | ICO 字节缓存：`(text,color)` → `byte[] ico` 缓存（容量 2–3 LRU），命中跳过全部渲染仍 `new Icon(ms)` 每次新实例 | `IconRenderer` 静态缓存；AppContext 调用点零改动 | 数值/颜色不变的连续刷新（常态）0 次 GDI 渲染；30s 周期稳态趋零 | 缓存约 30–60KB；**必须**保持每次返回新 Icon 实例——SetIcon 会 Dispose 旧实例，返回缓存实例会复用已 Dispose 对象（实现红线） |
| M4 | `FontFamily("Segoe UI")` / 居中 `StringFormat` 静态化；自适应循环复用 family | `IconRenderer.RenderPng` L100–115 | 每帧省 1 FontFamily + 1 StringFormat.Clone；5 帧/次累计可观 | 无 |
| M5 | DetailForm 共享字体（行 9F/8.5F/加粗 3 个静态 Font） | `UpdateRelay` L308–315、L323 | 每次中转分组刷新省 4–8 次 `new Font`（GDI 句柄 churn） | 共享 Font 不可 Dispose（进程生命周期） |
| M6 | GC 调优：**`System.GC.ConserveMemory=5`**（取值 0–9、默认 0；键名经 dotnet/runtime 与官方文档核实——v1 键 `System.GC.Conserve` 不存在，照抄配置被静默忽略，R1-F1）+ `System.GC.Concurrent=false` | csproj ItemGroup | 工作集估 −10~25MB（**实现期实测为准，非已验证结论**） | 后台 GC 关闭 → 阻塞式回收；30s 低分配节奏无感；DATAS（net9+ 默认开，`System.GC.DynamicAdaptationMode`）作 A/B 基线见 §2.6；有时延回归即回退此项 |
| M7 | 工作集裁剪 `SetProcessWorkingSetSize(-1,-1)`：headless 启动后 + 每 10 分钟；UI 最小化 + 每 10 分钟 | HeadlessRunner / AppContext | 任务管理器数字立降（观感）；不改变物理驻留 | 页故障回入带来轻微 CPU/时延尖峰；明确标注「改善观感、非真实省内存」；**裁剪时点与 A10 采样窗口对齐：采样窗口 30 分钟 ≥ 裁剪周期 10 分钟，判定式含锯齿容差（R1-F10）** |
| M8 | 既有已达标项确认（不改动） | RequestLog 500 环形 / 探测 Task.Delay / SetIcon 旧 Icon Dispose / SocketsHttpHandler PooledConnectionLifetime 10min / AdminResource Html 缓存 | — | 防回归：验收走查锚点 |

拒绝项：`InvariantGlobalization=true`（文化行为变化风险大于几 MB 收益）；`HeapHardLimitPercent`（OOM 风险）；请求体 ArrayPool 化（复杂度收益比差，留待实测超标再说）。

## 7. 风险与红线核查

**技术风险**：

| # | 风险 | 评估 | 缓解 / 立场 |
|---|---|---|---|
| R1 | Session 0 下 HttpListener 可用性 | **低**：HttpListener 走内核 http.sys，无会话亲和；回环 `http://127.0.0.1:8787/` 前缀在普通用户下已被生产验证可用（README 既有承诺），SYSTEM 权限只高不低 | 失败症状 = `Start` 返回 false → 崩溃日志 + UI 探活失败可见；无静默 |
| R2 | SYSTEM 出站受限：用户级代理不可见；**开机早期时钟未同步 → 出站 TLS 证书时间校验失败（R1-F5 并入）** | 中（代理强依赖网络；时钟偏差限于开机窗口期，两者症状同形需一并排查） | §5 E6/S3 已知限制 + 文档标注 + 降级路径；随网络就绪/时间同步自愈 |
| R3 | UAC 交互打断/被策略禁止 | 低–中 | §2.2 双回退（HKCU Run / 手动命令） |
| R4 | 双实例配置写竞争 | 低 | §5 E5 精确化论证（R1-F4）：重叠期可达成的写入均为单字段合并，全节点写（token 生成/播种）在 S4 前提下不可达；原子写 + 布尔自愈，不加文件锁 |
| R5 | dev 隔离被绕过 | 低 | 三重硬禁：`--dev --headless`、`--dev --task-*` exit≠0；dev UI 不创建自启动区块；任务名/互斥体唯一 |
| R6 | 发布产物回归 | 低 | 零 NuGet、csproj 仅 ItemGroup 增运行时项；`PublishSingleFile` 属性不动 |
| R7 | 卸载/注销残留 | 低 | 注销流 = `/End` + `/api/shutdown` + `/Delete /F`；exe 被直接删除的孤儿任务在开机时仅任务计划程序报错，无害；手动清理命令写入开发轮次 README |
| R8 | 旧版 `shell:startup` 手工启动共存 | 低 | 委托态防双 relay（只有一个 8787 监听者）；提示用户可移除手工快捷方式（开发轮次 README 说明） |
| R9 | headless 无优雅停机，关机丢 in-flight | 低 | 与今天断电语义一致；SSE 客户端本就处理断流；非目标已声明 |
| R10 | `/status` 形状变化破坏旧消费者 | 低 | `headless` 字段纯新增、默认省略/false；既有字段零变化（对齐 `forced` 字段先例） |
| R11 | `/api/shutdown` 被滥用：免鉴权故障态 + 回环 DNS rebinding（简单 POST 无自定义头、无 CORS 预检） | 低（概率低但后果是「杀进程」级） | 端点单点加固（R1-F6）：`EffectiveToken` 为空直接 403 + Host 头必须等于 `127.0.0.1:<port>`；Bearer 语义同既有 /api/* |

**红线核查**：

- headless 进程零 WinForms 加载（R1-F3）：`RunUi` 抽取后 Main 的 IL 无 WinForms 可达引用；验收锚点 = 进程模块列表不含 System.Windows.Forms.dll（A2）。
- 明文 key 不进 git 仓库内任何文件：本设计零新增 key 落盘点；`--config-dir` 是路径非 key；冻结方案（B）被否决强化此线。
- key 只写不读、日志/API 掩码：headless 复用同一 AdminApi/ProviderRuntime 掩码路径，零改动；`/api/shutdown` 响应不含任何敏感字段，且有空 token 403 + Host 校验单点加固（R1-F6）。
- 配置写回唯一路径 `RelayOptions.Persist`：headless/UI 共用它，委托态 UI 零写。
- 仅监听本机回环：headless 同前缀 `http://127.0.0.1:{port}/`。
- 日志与错误汇总不含请求体内容：headless 无新增日志面（仅崩溃堆栈）。
- 部署绝不改动 opencode.json 既有 provider 条目：`OpenCodeDeployer` 零逻辑改动，仅 home 重定向（SYSTEM 下反而修复了「部署落 systemprofile」的潜在错误）。
- GC 配置键名以官方文档为准（R1-F1 教训）：`System.GC.ConserveMemory` / `System.GC.DynamicAdaptationMode` / `System.GC.Concurrent` 均经官方 GC 配置文档核实；错误键名会被运行时静默忽略、无任何报错。

## 8. 验收标准（可测试条目）

环境：Windows 11，生产 exe 目录外测试；dev 实例全程不参与 A1–A17（另有 A7）。

| # | 条目 | 验证方法 |
|---|---|---|
| A1 | `GlmBalanceTaskbar.exe --headless --config-dir %USERPROFILE%` 手工启动：`/health` 返回 200；`netstat -ano` 仅 headless PID 监听 8787；进程无窗口/无托盘图标 | curl + netstat + 任务管理器观察 |
| A2 | headless 不做 UI 配额轮询且**不加载 WinForms**：① 源码走查锚点——headless 路径不构造 30s `System.Windows.Forms.Timer`/不 new `NotifyIcon`；`intervalSeconds` 不生效于 headless；② **headless 进程模块列表不含 `System.Windows.Forms.dll`（R1-F3：验证 `RunUi` 抽取到位、Main 的 IL 无 WinForms 可达引用——M1 收益的结构性前提）** | 代码评审锚点 + PowerShell `(Get-Process -Id <pid>).Modules` / Process Explorer |
| A3 | UI 注册流：点注册 → UAC 同意 → `schtasks /Query /TN GlmBalanceTaskbarRelay` exit 0；`/TR` 含 `--headless` 与 `--config-dir <profile>`；任务以 SYSTEM 运行（任务计划程序 UI 可见）；**注册返回后 ≤60s 内 `/health` 200（`/Run` 立即拉起生效，R1-F7）** | schtasks /Query /L + 图形界面 + curl |
| A4 | 未登录运行：重启 → 登录界面停留 ≤2 分钟内 `curl /health` 200；`/status` 中 zhipu-coding `HasKey=true`（auth.json 经 profile 解析） | 重启 + curl + /status 检查 |
| A5 | 委托态：headless 运行时启动 UI——`netstat` 仍仅 headless 一个 8787 监听者；详情窗渠道行渲染、relay 开关禁用且提示文案可见；右键渠道行无 pin 菜单；**点击详情窗「管理面板」按钮、托盘菜单项、缺 key 引导按钮 `_btnRelayGuide` 三者任一 → 打开 `http://127.0.0.1:<port>/admin`（远端面板功能可用），不弹「中转未启用」提示（R1-F2）** | netstat + UI 走查 + 浏览器 |
| A6 | UI 退出不影响 headless：委托态退出 UI，60s 后 `/health` 仍 200 | curl 前后对比 |
| A7 | dev 隔离：`--dev --headless`、`--dev --task-register`、`--dev --task-unregister` 均 exit ≠ 0 且有错误输出；`--dev` UI 详情窗无「自启动」区块 | 命令行 + UI 走查 |
| A8 | 注销清理：注销流结束后 `schtasks /Query` exit 1、`/health` ≤5s 内失联、后台进程消失；随后 UI 可正常本地启用 relay | schtasks + curl + netstat |
| A9 | UAC 取消回退：注册时取消 UAC → `schtasks /Query` exit 1（零残留）→ 回退对话框出现且可选「登录时启动」；选择后 HKCU Run 键写入、二次启动勾选态正确 | 注册表 reg query + schtasks |
| A10 | headless 内存（**口径与判定式明确化，R1-F10**）：注册任务并登录态闲置 30 分钟（有探测流量），PowerShell `Get-Process -Id <headlessPid>` 每 60s 采样 10 次。**计数器 = `WorkingSet64`**（Get-Process 原生属性，无性能计数器本地化/可用性问题；对应「工作集」口径，较「专用工作集」系统性偏高 10–20MB 共享页——M1 的 35–60MB 估算为专用工作集口径，故阈值换算为 **首次 ≤80MB**）。**判定式：① 首次采样 ≤80MB；② 10 次采样线性拟合斜率 ≤ 0，且末 3 次均值 ≤ 首次 + 10MB**（容差吸收 M7 每 10 分钟裁剪的锯齿；采样窗口 30 分钟 ≥ 裁剪周期 10 分钟） | PowerShell `Get-Process` 循环采样 |
| A11 | UI 内存/GDI：同一百分比连续 30 次刷新，任务管理器 GDI 对象数与私有工作集无增长趋势；`--dump-icon` 输出与优化前逐字节同形（ICO 仍 5 帧） | GDI 对象列 + dump 对比 |
| A12 | 单写者：委托态 UI 全程操作（开关窗/刷新/开面板）后 `glmbalance-taskbar.json` 的 LastWriteTime 不变 | 文件属性前后对比 |
| A13 | `/status` 兼容：headless `/status` 与升级前形状对比仅多 `headless` 字段；`--once`、`--relay-init`、`--dump-icon`、`--dev` 全套既有验收复跑通过 | jq/人工对比 + 既有验收回归 |
| A14 | 发布：`dotnet publish -c Release -r win-x64 --self-contained false` 产单文件 exe；csproj 无 `PackageReference` 增量 | 构建产物检查 |
| A15 | **S4 启动竞态收敛（R1-F9）**：headless 手动启动后 5s 内启动 UI → 30s 内 `netstat` 收敛为仅 headless 一个 8787 监听者，详情窗最终呈现委托态（开关禁用 + 远端渠道行渲染），期间「端口占用」警告不静默 | netstat + UI 走查 |
| A16 | **S5 委托态回退 + 重载配置（R1-F9/R1-F8）**：委托态下经 /admin 面板新增一个 custom-relay 渠道（headless 写盘）→ 停掉 headless（`/api/shutdown`）→ UI 连续 N 次探活失败回退本地 Start → `netstat` 显示 UI 进程监听 8787，**详情窗含新渠道（证明回退前重载了配置，而非构造期旧快照）** | netstat + UI 走查 |
| A17 | **G4 key 轮换跟随（R1-F9）**：headless 运行中修改 auth.json 的 key（换另一有效 key）→ 重启 headless → `/status` 该渠道 `HasKey=true` 且探测恢复（或经 admin 面板 /test 用新 key 成功）——证明 §2.3 方案 A 跟随轮换、非注册时冻结 | /status + admin /test |

**验收覆盖七问对照**：Q1→A1/A2/A13，Q2→A3/A4/A8/A9，Q3→A4(HasKey)/A17/E1，Q4→A5/A6/A12/A15/A16，Q5→A3/A8/A9/A7，Q6→A10/A11，Q7→R1–R11 表 + A13/A14。
