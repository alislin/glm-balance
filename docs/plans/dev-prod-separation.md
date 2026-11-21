# 设计草稿：开发环境与部署环境分离（dev-prod-separation）—— 设计 v2

- 状态：**已评审通过（R2 APPROVED），随实现 commit 落库**。基于评审 R1（2 major、5 minor，结论 REVISE）修订，经评审 R2（3 minor：N1/N2/N3，均按裁决落实）通过。
- ~~v1 文档保留于 `docs/plans/dev-prod-separation.md` 未动；本文档为以 v1 为基础的修订版，
  未涉及的章节原样保留。~~（本文件即 v2 正文；v1 仅存于编排目录归档。）
- 前提：生产实例 `D:\bin\local-relay\GlmBalanceTaskbar.exe`（PID 12212，监听 127.0.0.1:8787）
  正在承载真实中转流量，**全程不可被干扰**。
- 已确认决策（不可推翻）：
  1. 配置隔离：开发实例配置文件放**其它目录**（不是 exe 同目录那份生产配置）；
  2. 开发判定：**命令行参数 `--dev`**（不带参数 = 完整生产行为，现状不变）；
  3. mock 范围：上游 provider 请求（chat/completions、探测/用量、models）全部 mock；
     管理 API（/admin 与 /api/*）保留真实；部署写盘（OpenCodeDeployer/PiDeployer）在开发实例 dry-run。

### 修订摘要（轮次 2）

| Finding | 级别 | 处置 |
|---|---|---|
| F1 运行时 provider 变更路径绕过 mock 覆写 | major | **已落实**：覆写点收敛为建池时强制（`BuildPool` 内兜底），配置加载 / import / PUT 三条入池路径统一覆盖（§3、§4.1、§5.2、验收 5b） |
| F2 `/status` dev 字段"可选增强"与验收 3 矛盾 | major | **已落实**：采纳评审推荐前者——`RelayStatus` 增加 `Dev` 字段升为**必做**改动（§3、§5.1；验收 3 保持原断言） |
| F3 "PUT port 经 SetEnabled→Start 拦截"表述与现状不符 | minor | **已修正**：改述为 PUT port 只写回不重启，拦截发生在其后任意一次 `Start()`（§3 待定决策、验收 3b） |
| F4 `--dev --once` 未列 RunOnce 改造点 | minor | **已补充**：§3 Program.cs 行显式列出 RunOnce 的 mock 快照替换落点（L187/L218 两处出站调用） |
| F5 RunRelayInit 内 `AppConfig.Load()` 调用点未点名 | minor | **已补充**：§3 ④ 点名 Program.cs L117（RunRelayInit）与 L164（RunOnce）为按模式传目录的调用点 |
| F6 mock upstream 线程/死锁论证缺失 | minor | **已补充**：§5.5 新增线程模型条目（异步 accept + 每连接 Task，无死锁面） |
| F7 `--dev --relay-init` dev 语义实现落点缺失 | minor | **已补充**：§3 DevMode/Program 行增加 relay-init dev 后处理（对 CreateDefault/SeedDefaults 产物覆写 port=8788 + mock 播种后再落盘） |

---

## 1. 目标 / 非目标

### 目标

- G1 `--dev` 启动的开发实例可与生产实例**同时运行**，互不感知、互不干扰。
- G2 端口区分：开发实例的中转监听端口默认 **8788**（≠ 生产 8787），且 dev 模式下
  硬性拒绝监听 8787。
- G3 单实例检测隔离：`--dev` 使用独立互斥体/事件名，二次启动恢复的是**同模式**
  实例；生产实例的互斥体/事件不受影响。
- G4 上游 mock：开发实例下 chat/completions、/v1/models、渠道测试（testProvider）、
  用量探测（zhipu-monitor）、托盘用量监控**均不打真实上游**（open.bigmodel.cn、
  真实 provider baseURL），统一打到进程内本地回环假上游。**覆盖全部入池路径**
  （启动加载、面板导入、面板编辑，R1-F1）。
- G5 部署写盘 dry-run：开发实例下 opencode/pi 部署只预览、绝不落盘
  （`~/.config/opencode/opencode.json` 等真实配置零改动）。
- G6 生产路径零变化：不带 `--dev` 时，所有行为与现状逐字节一致（配置位置、端口、
  互斥体名、AUMID、真实上游、部署可落盘）。

### 非目标

- 不做自动化 UI 测试框架、不打包独立 dev 发行版（同一 exe 参数化即可）。
- 不重构生产代码的依赖注入结构（接口抽象大改造被否决，见 §2.3）。
- 不改变生产实例的任何配置/部署/行为；不迁移生产配置文件。
- mock 假上游不做协议级完备模拟（只覆盖本仓库实际用到的端点与响应形态）。
- v1 不提供 dev 配置的动态热改管理端点（改 `devMock` 段 + 重启 dev 实例即可）。

---

## 2. 方案选项与取舍

### 2.1 配置来源如何切换

| 选项 | 说明 | 优点 | 缺点 | 结论 |
|---|---|---|---|---|
| A. 环境变量（`GLMBALANCE_DEV=1` / `GLMBALANCE_CONFIG_DIR`） | 启动前设环境变量 | 不改 CLI；可任意重定向 | 与已确认的 `--dev` 判定形成**两个真源**；环境变量残留会波及同 shell 启动的其它进程；误设后生产实例可能悄悄改配置源，危险面更大；快捷方式上不可见 | 否 |
| B. 配置文件名派生（exe 同目录 `glmbalance-taskbar-dev.json`） | 只改文件名，实现最小 | 改动极小 | **违背已确认决策**（仍在 exe 同目录 = 生产目录 `D:\bin\local-relay`，写盘仍落生产目录，触碰红线）；dev/prod 配置同目录，误备份/误删除同时波及两版 | 否 |
| C. 常量目录（`--dev` → `%LOCALAPPDATA%\GlmBalanceTaskbar-dev\`） | 代码内单一派生点 | 完全隔离（符合已确认决策）；与生产目录、仓库生命周期彻底解耦；可预测、易文档化 | 位置不直观（需文档与 `/status` 提示）；多一份配置要维护 | **推荐** |

**推荐 C**：`DevMode.ConfigRoot = %LOCALAPPDATA%\GlmBalanceTaskbar-dev\`，配置文件为
其下 `glmbalance-taskbar.json`（与生产同名但不同目录，复用全部解析/写回代码）。
`Config.Load()` 增加可选参数 `Load(string? configDir = null)`，默认 null = 现状
（exe 同目录优先），dev 传入 ConfigRoot——生产路径零变化。
v1 不加 `--dev-config <dir>` 逃生舱（列为后续可选增强，避免首版参数面膨胀）。

#### 开发配置目录：`%LOCALAPPDATA%` vs 仓库内 bin 外目录

| 候选 | 权衡 |
|---|---|
| `%LOCALAPPDATA%\GlmBalanceTaskbar-dev\`（推荐） | 物理隔离最彻底（生产 exe 目录、仓库目录都无关）；`git clean`/rebuild/换分支均不波及；符合 Windows 应用数据惯例；含假 key 的配置也无误提交风险。缺点：位置隐匿 → 用 `/status` 与本文档弥补 |
| 仓库内（如 `taskbar\dev-config\`） | 可见、可版本化示例；但 rebuild/clean 可能误删；存在把含 key 配置误提交 git 的风险；与"完全隔离"的诉求次序不符 |

### 2.2 互斥体名如何派生

| 选项 | 结果 | 结论 |
|---|---|---|
| 前缀 `Local\dev.GlmBalanceTaskbar.SingleInstance` | 排序时 dev 条目与生产分离 | 可行 |
| 后缀 `Local\GlmBalanceTaskbar.SingleInstance.Dev` | 原名保持前缀整体可读，diff 最小 | **推荐** |

**推荐后缀**。关键是**单一派生点**：新增静态类 `DevMode`，`Program.cs` 的
`MutexName` / `ShowEventName` 两个硬编码常量改为按 `DevMode.IsDev` 派生的静态属性
（生产值逐字节不变）：

```
MutexName      : Local\GlmBalanceTaskbar.SingleInstance      → .Dev 后缀
ShowEventName  : Local\GlmBalanceTaskbar.ShowWindow          → .Dev 后缀
AUMID          : AlisL.GlmBalanceTaskbar                     → .Dev 后缀
```

`Program.cs` L42-56（mutex 抢占 + `EventWaitHandle.OpenExisting(ShowEventName)` 通知
恢复窗口）与 L61（创建 ShowEventName）全部经派生属性取值，逻辑零改动——二次启动
只会通知**同模式**实例恢复窗口（AppContext.ShowMainWindow 不变）。

### 2.3 mock 如何注入

| 选项 | 说明 | 优点 | 缺点 | 结论 |
|---|---|---|---|---|
| A. 接口抽象（`IUpstreamClient` / `IUsageSource` 注入 fake） | 重构 RelayServer `_upstream`、RelayManager `TestClient`、`GlmApiClient` 三个出口为接口 | 测试替身最正统 | 改动波及生产代码主干结构，违背 G6"现状不变"的低风险精神；三个出口语义不同需要三套抽象 | 否 |
| B. `if (DevMode.IsDev)` 分支散落 | 各出口内分支返回假数据 | 改动小 | 判断散落多处难维护；请求**不经过真实 HTTP 栈**，RelayServer 的转发/SSE/超时/转移逻辑完全测不到——恰好漏掉最需要测的部分 | 否 |
| C. 本地回环假上游 + 配置覆写 | 进程内起一个 mock HttpListener（默认 **127.0.0.1:8789**）；把全部 providers `baseURL` 与全局 `MonitorBase` **强制覆写**指向它（v2：覆写收敛到**建池时强制**，见 §4.1） | 生产转发代码**零改动**，chat/completions、models、探测、testProvider 全部走完整真实 HTTP 栈；行为可用 dev 配置 `devMock` 段编程（见 §4）；单一代码出口 | 多占一个端口（需自环校验）；需模拟智谱监控端点的 JSON 形态 | **推荐** |

**推荐 C**。理由：本仓库最需要验证的是 RelayServer 的渠道转移（failover）、SSE
转发、冷却/降级状态机——这些逻辑只有在请求真正流过 `TryUpstreamAsync` /
`ProbeLoopAsync` 时才会被触发，只有方案 C 能覆盖。GlmApiClient、ProviderRuntime、
IUsageProbe 等生产类型均不需要为 mock 改造。

---

## 3. 涉及模块与接口

> 原则：不带 `--dev` 时不新增任何行为分支（派生属性返回原常量值）。

| 文件 | 改动要点 |
|---|---|
| **新增 `DevMode.cs`** | dev 模式单一真源：`IsDev`（解析 args，`OrdinalIgnoreCase`，与既有 `--once` 等参数同风格）；`ConfigRoot`（`%LOCALAPPDATA%\GlmBalanceTaskbar-dev\`）；`ConfigPath`；`MutexName` / `ShowEventName` / `Aumid` 派生（生产值原样）；`DefaultRelayPort = 8788`；`MockBaseUrl`（`http://127.0.0.1:<upstreamPort>`，供建池覆写判定与覆写目标，R1-F1）；`IsMockBaseUrl(string url)` 判定函数；`ApplyMockOverrides(AppConfig)`（启动配置合成阶段，见 §4.1 第 1 层）；`ApplyPoolOverride(ProviderConfig)`（建池时单渠道覆写，供 BuildPool 调用，见 §4.1 第 2 层）；`MockSnapshot(AppConfig)`（`--dev --once` 用内置快照）；`ApplyDevRelayInitSemantics(RelayOptions)`（`--dev --relay-init` 的 dev 后处理：port=8788、播种渠道 baseURL→mock、假 key，R1-F7） |
| `Program.cs` | ① `Main` 早期解析 `--dev`（在 `--once` / `--dump-icon` / `--relay-init` 判定**之前**，使组合语义生效）；② `MutexName`/`ShowEventName` 常量改为 `DevMode` 派生属性；③ `AppUserModelId` → `DevMode.Aumid`；④ `AppConfig.Load()` 调用点按模式传配置目录——**三处逐一点名**：`Main` L58（主路径）、`RunOnce` L164、`RunRelayInit` L117（仅取全局 apiKey 读取链；dev 下传 ConfigRoot，避免读 exe 同目录生产配置、避免真实全局 key 成为 ResolveKey 回退来源，R1-F5）；⑤ **`RunOnce` mock 改造（R1-F4）**：dev 下不构造 `GlmApiClient`，`FetchUsageAsync()`（L187）与 `FetchActivityAsync()`（L218）结果替换为 `DevMode.MockSnapshot(cfg)`（activity 部分为内置静态数据），保证组合语义表"不打网络"有实现落点；⑥ 启动 dev mock upstream（`--dev` 且非 `--once`/`--relay-init` 时）；⑦ **`RunRelayInit` dev 后处理（R1-F7）**：`--dev --relay-init` 下，在 `RelayOptions.CreateDefault()` / `SeedDefaults()` 之后、`InitHeadless` 落盘之前调用 `DevMode.ApplyDevRelayInitSemantics(opts)`（否则默认产物是 port=8787 + 真实 baseURL 播种 zhipu-coding/custom-relay，配置文件本身就含真实上游 URL） |
| `Config.cs` | ① `Load(string? configDir = null)`：优先级第 1 层（exe 同目录）替换为传入目录，**其余回退链（auth.json → opencode.json → tui.json）保持不变**（只读，无泄漏风险）；② 新增顶层 `devMock` 段解析为 `DevMockOptions`（生产 `Load` 不读该键，JSON 未知键自动忽略，生产零影响） |
| `RelayManager.cs` | ① `configPath` 缺省派生改为经统一配置根（dev → ConfigRoot，生产 → 现状 exe 同目录）；② `Start()` 增加 dev 硬防护：`_opts.Port == 8787` → 启动失败 + warning「dev 实例禁止占用生产端口 8787」；③ **建池时强制覆写（R1-F1 核心修复）**：`BuildPool()` 内、dev 模式下对每个 `ProviderConfig.BaseUrl` 执行 `DevMode.ApplyPoolOverride`——非 mock 地址 → **内存覆写**为 `DevMode.MockBaseUrl` + 记 warning；此为最终防线，位于一切入池路径的汇合点（详见 §4.1）；`MonitorBase` 侧：`ProbeContext.Global` 持有的是启动时已覆写 MonitorBase 的 `AppConfig`，且 provider `BaseUrl` 被覆写为 127.0.0.1 后 `ZhipuMonitorProbe.MonitorBase(p)`（L72-82）不再匹配 zhipu 域 → 回退已覆写的 `Global.MonitorBase`，两条探测路径均落 mock；④ `CreateDeployers()` 在 dev 下返回 **DryRun 装饰器**包装（`new DryRunDeployer(new OpenCodeDeployer())` 等）——AdminApi 零改动即获得 dry-run 语义；⑤ `EvaluateOnce` 支持 mock 快照注入（`--dev --once`）；⑥ `Snapshot()`（RelayStatus 组装）填充 `Dev = DevMode.IsDev`（R1-F2） |
| `RelayStatus.cs` | **（R1-F2，必做）** `RelayStatus` record 尾部追加 `bool Dev = false` 字段（带默认值，生产构造点无需逐一改参）；`/status` 响应经 `RelayJson.Serialize(_mgr.Snapshot())` 自动携带 `dev` 字段 |
| **新增 `DevMock/MockUpstreamServer.cs`** | 进程内假上游（HttpListener，默认 127.0.0.1:8789）：`POST /v1/chat/completions`（含 SSE）、`GET /v1/models`、`GET /api/monitor/usage/quota/limit`、`GET /api/monitor/usage/model-usage`、`GET /api/monitor/credit-usage/activity`；按 `devMock` 规则引擎决定状态码/延迟/流中断（§4）；线程模型见 §5.5；启动前自环校验：`upstreamPort ∉ {relayPort, 8787}` |
| **新增 `DevMock/DryRunDeployer.cs`** | `IAgentDeployer` 装饰器：`PreviewAsync` 透传（本就只读）；`DeployAsync` → 调 `PreviewAsync` 并合成 `DeployResult(Success=true, BackupPath=null, Message="[dev] dry-run：仅预览未落盘\n" + preview.Summary)`，绝不触碰 `WriteAtomicAsync` |
| `AppContext.cs` | 窗口标题加 `[DEV] ` 前缀（`_form.Text` 与托盘 tooltip），消歧双实例；其余逻辑零改动（GlmApiClient 经 MonitorBase 覆写自动打到 mock） |
| `AdminApi.cs` | **零必须改动**（dry-run 在装饰器层拦截；`dev` 字段在 Snapshot 层携带，/status 序列化自动生效）；可选 UI 增强：admin 面板显示 DEV 徽标（读取 status.dev） |
| `RelayOptions.cs` | `Parse` 默认端口 8787 **保留不变**（生产语义）；dev 侧在配置合成阶段后处理：relay.port 缺失或 ==8787 → 置 8788 + warning（用户在 dev 配置显式写了非 8787 的其它端口则尊重）；`SeedDefaults()` 本体不改（真实 baseURL 播种是生产语义），dev 语义由 `DevMode.ApplyDevRelayInitSemantics` 在其后处理（R1-F7） |
| `Relay/Deploy/*` | 零改动（DryRun 装饰器在外层拦截） |
| `RelayServer.cs` / `ProviderRuntime.cs` / `Probes/*` / `GlmApiClient.cs` | 零改动（mock 经建池覆写 + MonitorBase 覆写自然生效） |

### `--dev` 与既有参数的组合语义

| 组合 | 行为 |
|---|---|
| （无参数） | 现状完整生产行为，**逐字节不变** |
| `--dev` | dev 配置目录 + `.Dev` 互斥体/事件/AUMID + relay 默认端口 8788 + mock 全开（建池强制覆写 + MonitorBase 覆写、启动 mock upstream）+ 部署 dry-run |
| `--dev --once` | dev 配置 + **内置 mock 快照**（RunOnce 不构造 GlmApiClient、不打网络，mock upstream 不启动）→ `EvaluateOnce` 纯离线评估；仍无监听、无写盘 |
| `--dev --relay-init <dir>` | 对 `<dir>\glmbalance-taskbar.json` 执行既有初始化例程，但经 `ApplyDevRelayInitSemantics` 生成 **dev 语义**配置：port=8788、providers 播种 mock 渠道（假 key `mock-<id>`、baseURL 指向 8789）；目标目录仍是显式参数（与 --dev 正交），不打任何真实接口 |
| `--dev --dump-icon` | 纯图标诊断，与 mock/配置无关，行为同现状 |
| `--dev` + 未知参数 | 同现状忽略 |

### 两个待定决策的推荐

- **开发实例默认端口 = 8788（固定默认 + dev 配置可覆盖 + 硬禁 8787）**。
  理由：固定默认保证"开箱即不冲突、可预测、易写文档"；dev 配置文件 `relay.port`
  可覆盖保留灵活性（8788 被其它软件占用时用户可自改）；`Start()` 硬校验拒绝 8787。
  **拦截机制准确表述（R1-F3 修正）**：`PUT /api/settings` 修改 port **只写回配置并
  返回 `restartRequired=true`，不触发重启**（AdminApi L341-345，当前监听不变）；
  端口 8787 被拦截的实际时点是**其后任意一次 `Start()`**——重启 dev 实例，或 relay
  未运行时在面板开 enabled（`SetEnabled(true) → Start`）。最终结论不变：dev 实例
  任何时刻都不可能监听 8787。
- **开发配置目录 = `%LOCALAPPDATA%\GlmBalanceTaskbar-dev\`**。理由见 §2.1 对比表
  （物理隔离最彻底、生命周期独立于仓库与生产目录、无误提交/误清理风险）。

---

## 4. mock 具体设计

### 4.1 假上游与规则引擎

dev 配置文件（`%LOCALAPPDATA%\GlmBalanceTaskbar-dev\glmbalance-taskbar.json`）新增顶层
`devMock` 段（生产实例解析时忽略该键）：

```json
{
  "relay": {
    "enabled": true, "port": 8788,
    "providers": [
      { "id": "zhipu-coding", "name": "Mock 渠道A", "priority": 10, "probe": "zhipu-monitor",
        "baseURL": "http://127.0.0.1:8789", "apiKey": "mock-zhipu-coding",
        "models": ["glm-5.3-flash"], "enabled": true },
      { "id": "custom-relay", "name": "Mock 渠道B", "priority": 20, "probe": "none",
        "baseURL": "http://127.0.0.1:8789", "apiKey": "mock-custom-relay",
        "models": ["glm-5.3-flash"], "enabled": true }
    ]
  },
  "devMock": {
    "upstreamPort": 8789,
    "chatContent": "[mock] dev 假响应",
    "latencyMs": 50,
    "quota": { "fiveHourRemainingPct": 80, "weekRemainingPct": 60 },
    "providers": {
      "zhipu-coding": { "chatStatus": 200, "modelsStatus": 200 },
      "custom-relay":       { "chatStatus": 200, "modelsStatus": 200 }
    }
  }
}
```

- **匹配键**：dev 配置给每个 provider 配**假 apiKey**（`mock-<id>`）。RelayServer 转发
  时 `Authorization: Bearer mock-<id>` → mock 上游按 key 反查 provider id → 应用该
  provider 的规则。不走真实全局 key 读取链（`ResolveKey` 显式 key 优先，天然满足）。

- **强制覆写（v2：三层防线，R1-F1 修复）**：

  | 层 | 位置 | 覆盖的入池/出站路径 | 性质 |
  |---|---|---|---|
  | 1. 配置合成阶段 | `AppConfig.Load` 之后经 `DevMode.ApplyMockOverrides(cfg)` | 启动加载路径：relay 段 providers、全局 `MonitorBase` | 便利层（提前warning、托盘/GlmApiClient 出口） |
  | 2. **建池时强制（最终防线）** | `RelayManager.BuildPool()` 内、`new ProviderRuntime(...)` 之前，dev 模式对每个 `ProviderConfig.BaseUrl` 执行 `ApplyPoolOverride`（非 mock 地址 → 内存覆写 + warning） | **一切入池路径的汇合点**：启动加载、`POST /api/providers/import`（AdminApi L82-127，Discovery 从真实 auth.json/opencode.json 读出真实 baseURL/key 直接 `MutateProviders(Add)`）、`PUT /api/providers/{id}`（L140-180，面板手填真实 baseURL/key）——三者均经 `PersistAndApply → ApplyOptions → BuildPool` 重建池，覆写必然生效；ProbeLoop 与 chat 转发只消费池内 `ProviderConfig`，不可能拿到未覆写的 URL | **安全层（本设计的出站隔离保证）** |
  | 3. 探测侧自洽 | `ProbeContext.Global`（构造时捕获已覆写 MonitorBase 的 cfg）+ `ZhipuMonitorProbe.MonitorBase(p)` 域名匹配 | provider BaseUrl 被覆写为 127.0.0.1 后不再匹配 zhipu 域 → 探测自行请求路径回退已覆写的 `Global.MonitorBase`；快照复用路径的数据源 GlmApiClient 亦走覆写后 MonitorBase | 推论层（无需额外代码，已核实自洽） |

  设计含义：**"配置合成阶段覆写"降级为便利层**（提前给出可读 warning、覆盖
  BuildPool 之外的 GlmApiClient/托盘出口），**出站隔离的正确性由第 2 层独立保证**——
  任何绕过配置阶段的运行时路径（import、PUT、未来新增的入池代码）都被 BuildPool
  汇合点兜住。warning 去重：同一渠道 id 只记一次（避免 ProbeLoop 日志刷屏）。

- **自环防护**：mock upstream 启动前校验 `upstreamPort ≠ relayPort` 且两者 ≠ 8787，
  违反则拒绝启动并报错。

### 4.2 chat/completions 返回什么

- 非流式（请求体 `"stream": false/缺省`）：固定最小 OpenAI 兼容 JSON——
  `id: "chatcmpl-mock-<n>"`、`object: "chat.completion"`、`model` 回显请求 model、
  `choices[0].message = { role: "assistant", content: <chatContent> }`、
  `finish_reason: "stop"`、`usage = { prompt_tokens: 10, completion_tokens: 8, total_tokens: 18 }`
  （固定值，保证 ActivityParser/统计路径有数可解析）。`latencyMs` 人为延迟后返回。
- 流式（`"stream": true`）：`Content-Type: text/event-stream`，发 N 个
  `data: {chunk}`（delta 增量拼接出 chatContent）+ `data: [DONE]\n\n`——覆盖
  RelayServer 的 SSE 转发路径。
- 按规则返回异常：`chatStatus: 429/402/403` → 配额类错误体（触发 `NoteQuotaFailure`
  硬冷却 + 转移）；`500` → `NoteErrorFailure` 计数；`stream-break` → 发 2 个 chunk 后
  直接断开 TCP（触发 `NoteStreamBroken`）。

### 4.3 探测返回什么

mock 上游模拟智谱监控端点，返回形状与 QuotaParser 期望一致的固定 JSON：

- `/api/monitor/usage/quota/limit`：按 `devMock.quota` 返回 5h（unit=3）剩余
  `fiveHourRemainingPct`%、周（unit=6）剩余 `weekRemainingPct`%。
- `/api/monitor/usage/model-usage`、`/api/monitor/credit-usage/activity`：固定最小
  数据（models 列表、累计/今日 tokens、近14天 spark）。
- 效果：`ZhipuMonitorProbe`（快照新鲜时复用 AppContext 快照、过期时自行请求）两条
  路都落在 mock 数据上 → 托盘图标显示 mock 数字；**把 `weekRemainingPct` 改到 < 5
  并重启 dev 实例，可观察渠道 `degraded`；改回 > 10 观察 `restore`**——软降级/恢复
  阈值逻辑（DegradeBelowPct / RestoreAbovePct）完全可测。

### 4.4 测试按钮（testProvider）行为

`TestProviderAsync` 是 `GET {baseURL}/models` → baseURL 已覆写为 mock →
`modelsStatus: 200` 返回池模型并集 → 面板显示「连接成功」；`modelsStatus: 401/429/500`
→ 对应失败文案。走同一规则引擎，无需特殊分支。

### 4.5 如何验证"测试流量切换"（failover）在 mock 下可测

1. dev 配置把 `zhipu-coding.chatStatus` 设为 `429`，重启 dev 实例。
2. 向 dev 实例发一条请求：
   `POST http://127.0.0.1:8788/v1/chat/completions`（Bearer dev 实例 localToken）。
3. 预期观测链（全部经 /admin 面板与 `/status` 只读接口）：
   - 渠道A 收到 mock 429 → `NoteQuotaFailure` → 状态 `cooldown`（剩余秒数可见）；
   - Router 转移到渠道B → mock 200 → 最终响应 200、内容 `[mock] dev 假响应`；
   - 请求日志（RequestLog）记录一次 failover，`今日转移` 计数 +1；
   - `quotaSeconds` 冷却到期后渠道A 惰性归位（`RefreshKey` 后仍持假 key，不再循环 401）。
4. 连续错误路径：`chatStatus: 500` 连打 3 次（ErrorThreshold）→ 渠道A 冷却
   `errorSeconds`，同样可在面板观测。
5. SSE 中断路径：`stream-break` → 断流后渠道A 错误计数 +1，客户端收到已转发的部分
   响应/连接中断，不崩溃。
6. **运行时入池路径的 mock 覆盖（R1-F1 回归点）**：
   - 面板「导入」：导入 Discovery 从真实 auth.json 发现的渠道（含真实 baseURL/key）
     → `GET /api/providers` 检查新渠道 `baseUrl` 已被覆写为 `http://127.0.0.1:8789`，
     warnings 出现覆写提示；`netstat -ano` 出站仍仅 8789；
   - 面板「编辑」：把某渠道 baseURL 手改回 `https://open.bigmodel.cn/api/coding/paas/v4`
     → 保存后 `GET /api/providers` 确认内存池中仍是 mock 地址（写盘文件里保留用户
     原值无害——落盘的是 dev 目录配置，且重启后第 1/2 层覆写再次生效）。

全程 dev 实例的出站连接只有 `127.0.0.1:8789`（`netstat -ano` 可证），零真实上游流量。

---

## 5. 风险

### 5.1 误带 `--dev` 在生产目录启动的后果与防护

- **后果推演**：exe 仍在 `D:\bin\local-relay` 但带 `--dev` → 配置读
  `%LOCALAPPDATA%\GlmBalanceTaskbar-dev\`（生产配置文件零读写）；relay 监听 8788
  （与生产 8787 无冲突）；互斥体/事件为 `.Dev`（与生产实例并存，互不通知）；上游全
  mock（不打真实接口）；部署 dry-run（不改真实 opencode 配置）→ **结果是多出一个
  全 mock 的第二实例，生产实例与生产配置均无感**。最坏情形只是任务栏多一个 [DEV]
  图标造成困惑。
- **防护**：① 配置写路径结构性收敛——dev 下 `RelayManager._configPath` 只可能派生自
  ConfigRoot，永不为 exe 同目录；② `Start()` 硬禁端口 8787；③ 窗口标题 `[DEV]` 前缀
  + `/status` 暴露 `dev=true`（**必做**，R1-F2：`RelayStatus.Dev` 字段 +
  `Snapshot()` 填充 `DevMode.IsDev`）；④ `--dev` 判定要求精确匹配参数
  （`OrdinalIgnoreCase`），不做环境变量推断，杜绝隐式进入 dev 模式。

### 5.2 生产实例永不受伤的论证

逐一核对 dev 实例的**全部写盘/外联点**：

| 写盘/外联点 | dev 下行为 | 是否触及生产 |
|---|---|---|
| `RelayManager.Persist`（token 生成/播种/设置写回，唯一配置写路径） | 写 `%LOCALAPPDATA%\GlmBalanceTaskbar-dev\glmbalance-taskbar.json` | 否 |
| `OpenCodeDeployer/PiDeployer.DeployAsync`（备份 + 原子写真实配置） | `DryRunDeployer` 拦截，仅 `PreviewAsync`（只读） | 否 |
| `--relay-init <dir>` | 目标目录为显式参数；文档规定 dev 用法指向 ConfigRoot；dev 下经 `ApplyDevRelayInitSemantics` 只产 mock 语义配置（R1-F7） | 否（与生产无关） |
| 崩溃日志 / `--dump-icon` | 写 `%TEMP%` | 否 |
| auth.json / opencode.json / tui.json 回退链 | 只读 | 否 |
| 出站网络（启动加载的池） | providers baseURL 与 MonitorBase 在配置合成阶段覆写 → 仅 127.0.0.1:8789 | 否 |
| **出站网络（运行时入池：import / PUT，R1-F1）** | **`BuildPool()` 建池时强制覆写为 MockBaseUrl（最终防线）**，import 带入的真实 baseURL/key、PUT 手填的真实 baseURL 均不可能成为出站目标；真实 key 仅可能作为 Authorization 头发往 127.0.0.1 mock（无外泄） | 否 |
| 监听端口 | 8788（relay）+ 8789（mock），启动前校验 | 否（≠8787） |
| 互斥体/事件 | `.Dev` 后缀 | 否（通知/抢占互不串扰） |

且编排/开发过程红线：不 kill、不重启生产进程；不向 127.0.0.1:8787 发写请求
（验证只走 GET /status、GET /health、GET /admin 只读）；不写 `D:\bin\local-relay\`。

### 5.3 AUMID / 窗口标题混淆

- dev AUMID 派生为 `AlisL.GlmBalanceTaskbar.Dev` → 双实例任务栏身份分离，图标不会
  互相覆盖。副作用：生产侧固定到任务栏的快捷方式（携带生产 AUMID）不会显示 dev
  实例的动态数字图标——dev 实例本就以测试为目的，可接受。
- 窗口标题 `[DEV] ` 前缀 + 托盘 tooltip 消歧；Alt-Tab / 任务栏均一眼可辨。

### 5.4 开发实例写盘路径泄漏到生产的排查

排查方法（实现后执行）：对 dev 实例做一次全生命周期操作（启动 → 改设置 → 导入渠道
→ 编辑渠道 → 部署 → 退出），期间用 Process Monitor 过滤 `D:\bin\local-relay\` 与
`~\.config\opencode\` 路径 → 预期**零写事件**（opencode.json 允许零读事件也成立，
因 dev 配置回退链仍会读它，只读无害）。同时 diff 生产配置文件 hash 前后不变。

### 5.5 其它风险

- **mock upstream 线程模型与死锁（R1-F6）**：MockUpstreamServer 复用 RelayServer
  同款模型——主循环 `GetContextAsync()` 异步 accept + 每连接一个 Task 处理，不占
  UI 线程（与 RelayServer 的 listener 线程相互独立）。同进程回环互调
  （RelayServer → mock upstream）双方均为异步 IO，无同步等待对方 UI 线程的情形，
  无死锁面。约束：mock handler 内不做长阻塞操作（`latencyMs` 延迟用
  `Task.Delay` 实现，SSE 用流式写出 + 间歇 `Task.Delay`），handler 内异常全部
  try/catch 吞掉并回 500，避免未观察异常拖垮进程。
- **mock 自环**：upstreamPort 被误配成 relayPort → 启动校验拒绝（§4.1）。
- **dev 配置误填真实 key**：mock 不校验 key 内容，真实 key 只会发给 127.0.0.1
  （且建池覆写保证 baseURL 也是 127.0.0.1）；文档仍建议 dev 用假 key；请求日志已有
  掩码机制，不打印全文。
- **8789 被占**：mock upstream 启动失败 → dev 实例报错退出 relay 启动（warnings
  可见），不静默回退真实上游——**宁可失败不可打真**（建池覆写仍在，即使 relay
  强行运行，出站目标也只是无人监听的 127.0.0.1:8789）。
- **`--dev --relay-init` 误指生产目录**：目标目录是显式参数，若误指
  `D:\bin\local-relay` 会写生产配置 → 文档明确 dev 用法 + 该路径属编排红线，
  实现后的验收步骤中不执行该组合于生产目录。

---

## 6. 验收标准（可操作步骤）

> 前置：生产实例（PID 12212，8787）保持运行；记录其 PID、配置文件 hash。
> 全程只允许对生产实例做只读 GET（/status、/health、/admin）。

1. **文档与范围**：本设计稿存在于编排目录（`20 设计 v2.md`）；实现轮次
   `git status` 仅新增预期文件，未改任何已跟踪文件。
2. **双实例并存**：运行 `GlmBalanceTaskbar.exe --dev` → 出现第二个托盘图标（标题含
   `[DEV]`），生产托盘图标与数字不受影响；任务栏两个独立身份（AUMID 已分离）。
3. **端口不同**：`netstat -ano | findstr "8787 8788 8789"` → 生产 PID 听 8787，
   dev PID 听 8788，mock 听 8789；`GET http://127.0.0.1:8788/status` 返回
   `dev=true`（R1-F2 必做项），`GET http://127.0.0.1:8787/status` 内容与实施前一致
   且**无 dev 字段**（生产响应形状不变）。
   **3b（R1-F3 拦截时点）**：dev 面板 `PUT /api/settings` 设 port=8787 → 返回
   `restartRequired=true`、当前监听仍为 8788；重启 dev 实例 → `Start()` 失败 +
   warning「dev 实例禁止占用生产端口 8787」、relay 不监听任何端口；改回 8788 重启
   → 恢复正常。
4. **互斥互不干扰**：
   - 再跑一次不带参数的 exe → **生产**实例窗口恢复，dev 实例无反应；
   - 再跑一次 `--dev` → **dev** 实例窗口恢复，生产实例无反应。
5. **mock 生效**：
   - dev 下 `POST :8788/v1/chat/completions`（Bearer dev localToken）→ 200，内容含
     `[mock]`；`GET :8788/v1/models` → 池模型列表；
   - `netstat -ano` 观察 dev PID 的出站连接 → 仅 127.0.0.1:8789，无外网地址；
   - dev admin 面板「测试」按钮 → 「连接成功」，且无真实出站。
   - **5b（R1-F1 运行时入池路径）**：
     - 面板「导入」真实发现的渠道 → `GET /api/providers` 确认新渠道 baseUrl 已
       被覆写为 `127.0.0.1:8789`，出站连接仍仅 8789；
     - 面板「编辑」把某渠道 baseURL 改回真实地址 → 保存后内存池中仍为 mock 地址，
       出站连接仍仅 8789。
6. **failover 可测**：设 `zhipu-coding.chatStatus=429` 重启 dev → 发一条请求 →
   面板显示渠道A `cooldown`、日志出现转移到渠道B、今日转移 +1（§4.5 全链）。
7. **部署 dry-run**：dev admin 面板执行「部署到 opencode（含设默认）」→ 返回预览
   文案（带 `[dev] dry-run` 字样）；`~/.config/opencode/opencode.json` 内容与 mtime
   不变，无新增 `.bak-*`。
8. **配置隔离**：dev 面板修改设置（如 probeIntervalSeconds）→ 仅
   `%LOCALAPPDATA%\GlmBalanceTaskbar-dev\glmbalance-taskbar.json` 变化；
   `D:\bin\local-relay\glmbalance-taskbar.json` hash 与 mtime 前后一致。
9. **`--dev --once` 不打网络（R1-F4）**：断网或防火墙出站阻断环境下运行
   `--dev --once` → 正常输出评估结果（mock 快照），无任何出站连接，退出码 0。
10. **`--dev --relay-init` dev 语义（R1-F7）**：对临时目录执行
    `--dev --relay-init <tmp>` → 检查生成的 `glmbalance-taskbar.json`：
    `relay.port == 8788`、providers 的 baseURL 均为 `http://127.0.0.1:8789`、
    apiKey 均为 `mock-` 前缀假 key；文件中不含任何真实上游 URL。
11. **生产零影响收尾核验**：生产 PID 仍为 12212（未重启）；生产配置文件 hash 不变；
    `GET :8787/status` 正常；生产实例的今日请求数未因 dev 测试增加。
12. **生产路径回归**：不带参数启动检查（在确保不与生产互斥冲突的方式下，或以代码
    走查替代）：`MutexName`/`ShowEventName`/AUMID/端口默认/配置路径五项派生值与
    现状逐字节一致。

---

## 附录 A：评审 R1 Findings 处置对照（逐条回应）

| # | 级别 | 评审意见要点 | 处置 | 落点 |
|---|---|---|---|---|
| F1 | major | 运行时 import/PUT 注入真实 baseURL+key 绕过配置阶段覆写，可能真实出站 | 采纳：覆写收敛为建池时强制。`BuildPool()` 是一切入池路径（初始加载 / import `MutateProviders` / PUT `MutateProviders`）经 `ApplyOptions` 的汇合点，在此对每个 `ProviderConfig.BaseUrl` 做「非 mock → 内存覆写 + warning（按 id 去重）」；`MonitorBase` 经 `ProbeContext.Global`（启动时已覆写）+ `ZhipuMonitorProbe.MonitorBase(p)` 域名不再匹配的推论自洽保障（评审"已核实"段亦确认此推论）。配置阶段覆写降级为便利层保留 | §3 RelayManager 行③、§4.1 三层防线表、§4.5 第 6 条、§5.2 表、验收 5b |
| F2 | major | `/status` dev 字段列为可选但验收 3 硬性断言，自相矛盾 | 采纳评审推荐（前者）：升为必做。`RelayStatus` record 尾部追加 `bool Dev = false`（带默认值使生产构造点零改动），`Snapshot()` 填 `DevMode.IsDev`；验收 3 保持原断言，并新增"生产 /status 无 dev 字段"反向断言 | §3 RelayStatus 行（新增）、RelayManager 行⑥、§5.1 防护③、验收 3 |
| F3 | minor | "PUT port 经 SetEnabled→Start 拦截"与现状不符（PUT port 只写回不重启） | 修正表述：拦截实际发生在其后任意一次 `Start()`（重启 dev 实例或面板重新启用）；最终结论不变。新增验收 3b 按正确机制验证拦截时点 | §3 待定决策推荐、验收 3b |
| F4 | minor | `--dev --once` 声称不打网络但未列 RunOnce 改造点 | 补充：§3 Program.cs 行⑤ 显式列出 RunOnce 改造——dev 下不构造 GlmApiClient，`FetchUsageAsync()`（L187）/`FetchActivityAsync()`（L218）替换为 `DevMode.MockSnapshot(cfg)` | §3 Program.cs 行⑤、组合语义表、验收 9 |
| F5 | minor | RunRelayInit 内无参 `AppConfig.Load()`（L117）未点名 | 补充：§3 ④ 点名三处调用点（Main L58 / RunOnce L164 / RunRelayInit L117），dev 下均传 ConfigRoot | §3 Program.cs 行④ |
| F6 | minor | mock upstream 线程/死锁论证缺失 | 补充 §5.5：异步 accept（GetContextAsync）+ 每连接 Task，与 RelayServer 同款模型，不占 UI 线程，同进程回环无死锁面；handler 内无长阻塞操作（延迟用 Task.Delay）、异常全捕获 | §5.5 第一条 |
| F7 | minor | `--dev --relay-init` 期望 dev 语义配置但无实现落点（CreateDefault/SeedDefaults 产 8787+真实 baseURL） | 补充：§3 DevMode 行 `ApplyDevRelayInitSemantics(RelayOptions)`、Program.cs 行⑦——在 CreateDefault/SeedDefaults 之后、InitHeadless 落盘之前应用（port=8788、播种渠道 baseURL→mock、假 key）；SeedDefaults 本体不改（生产语义）；新增验收 10 | §3 DevMode/Program 行、组合语义表、验收 10 |
