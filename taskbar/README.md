# GlmBalanceTaskbar（任务栏常驻）

把智谱 GLM Coding Plan 的**周积分剩余 %** 实时显示在 Windows 11 任务栏上的 C# 小程序，与 opencode 插件 [glm-balance](../README.md) 同源数据。

## 特性

- 应用以最小化状态常驻任务栏，图标实时渲染剩余百分比（如 `52%`），按剩余量着色：绿 > 40% / 黄 15–40% / 红 < 15%
- 任务栏按钮叠加**原生进度条**显示剩余比例
- 悬停任务栏图标显示摘要（窗口标题即 tooltip）：`GLM 周 52% · 已用 32016/66000 · ↻ 周五 14:14`
- **系统托盘同步图标**（右下角）：同样的动态数字，左键打开详情，右键菜单（打开详情 / 立即刷新 / 退出）
- **托盘悬停多行摘要浮窗**：周 / 5h / 活跃度（累计·今日·连续）/ 更新时间一屏尽览，鼠标移开自动消失
- 点击图标弹出**详情窗口**：5h 积分 / 周积分 / MCP 月度进度条与重置时间、24h tokens、分模型明细、活跃度（累计 / 今日 / 连续天数 / 近 14 天用量柱状图）
- 关闭窗口 = 最小化常驻，退出走窗口内"退出"按钮或托盘菜单；重复启动会恢复已有窗口（单实例）

> 原理：任务栏按钮显示的是运行中窗口的当前图标（`WM_SETICON`），运行期间可随时更换；"固定到任务栏"仅保留槽位，动态数据需程序保持运行。

## 配置来源（零配置开箱即用）

按优先级自动读取，全部可选：

| 来源 | 提供内容 |
| --- | --- |
| exe 同目录 `glmbalance-taskbar.json` | 覆盖任意字段（见下表） |
| `~/.local/share/opencode/auth.json` | API key（同插件 e2e-check，条目名含 zhipu/bigmodel/z.ai，优先 `zhipuai-coding-plan`） |
| `~/.config/opencode/opencode.json` | provider 的 apiKey / baseURL |
| `~/.config/opencode/tui.json` | glm-balance 插件条目的 organization / project 等选项（同插件 deploy 配置） |

`glmbalance-taskbar.json` 字段：

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `apiKey` | 自动读取 | 智谱 API key（`Authorization` 请求头） |
| `organization` / `project` | 读 tui.json | 团队版组织 / 项目 ID 请求头 |
| `usageType` | 有 organization 时 `2` | 用量维度：`2`=团队 `1`=个人 |
| `monitorBase` | `https://open.bigmodel.cn` | 监控 API 基地址 |
| `intervalSeconds` | `30` | 刷新间隔（5–3600 钳制） |
| `activity` | `true` | 活跃度开关 |
| `activityType` | `3` | 活跃度查询维度 |

示例：

```json
{
  "apiKey": "",
  "organization": "org-xxxxxxxx",
  "project": "proj_xxxxxxxx",
  "intervalSeconds": 60
}
```

## 构建与发布

要求 .NET 10 SDK。

```sh
# 开发构建
dotnet build taskbar/GlmBalanceTaskbar -c Release

# 发布单文件 exe（框架依赖，约 220KB，目标机需 .NET 10 Desktop Runtime）
dotnet publish taskbar/GlmBalanceTaskbar -c Release -r win-x64 --self-contained false
```

## 使用

1. 运行 `GlmBalanceTaskbar.exe` → 程序最小化常驻，任务栏出现用量图标，**系统托盘同步出现数字图标**（Win11 默认收进托盘溢出菜单 `^`，想让其常驻右下角需在 设置 > 个人化 > 任务栏 > 其他系统托盘图标 中开启 GlmBalanceTaskbar）
2. **右键任务栏上的运行按钮 → 固定到任务栏**（程序已声明显式 AUMID，固定时会一并写入快捷方式，此后固定按钮在运行时直接显示动态数字图标）
3. 开机自启：`Win+R` 输入 `shell:startup` → 把 exe 快捷方式放入启动文件夹
4. 托盘图标：左键单击打开详情窗口；右键菜单 = 打开详情 / 立即刷新 / 退出

## 固定后的图标行为

- 程序**运行中**：固定按钮显示动态数字（周积分剩余 %，绿/黄/红着色）
- 程序**未运行**：固定按钮显示静态 `G` 占位图标——属正常预期，启动后即恢复动态显示
- 若旧版本固定按钮一直显示静态 `G`（运行时也不例外）：**取消固定 → 启动最新版 → 从运行中的任务栏按钮重新固定**一次即可（旧快捷方式缺少 AUMID 属性）
- 开始菜单的固定项始终显示静态图标（开始菜单不展示动态数据），属正常现象

## 调试

```sh
# 控制台抓取一次并打印解析结果（对齐 scripts/e2e-check.ts 输出）
dotnet run --project taskbar/GlmBalanceTaskbar -- --once
```

输出示例：

```
config: base=https://open.bigmodel.cn org=org-cAa17C… project=proj_AC47bb… type=2 interval=30s activity=True
quota limits: CREDIT_LIMIT/unit=3/2%, CREDIT_LIMIT/unit=6/63%
level: pro
5h  : 98% · 已用 427/15000 · ↻ 09-25 02:38
week: 37% · 已用 42056/66000 · ↻ 周五 14:14
24h : -
activity: 累计 3.52G · 149h46m | 今日 59M | 连续 25 天 | 近14天 ▆▇▄▂▃▃▅▄▁██▆▅▃
OK
```

## 故障排查

| 现象 | 处理 |
| --- | --- |
| 图标显示 `?`，悬停见错误信息 | API 失败：检查网络 / apiKey 是否有效 / 团队版 organization 头是否配置 |
| 悬停提示"未找到 API key" | 按 `glmbalance-taskbar.json` 显式配置 `apiKey` |
| 任务栏按钮消失 | 程序未运行；重新启动 exe |
| 固定按钮显示静态 `G`（运行中也不变） | 旧固定快捷方式缺少 AUMID：取消固定 → 启动最新版 → 从运行中的任务栏按钮重新固定 |
| 图标数字显示不全/异常 | 确认运行的是最新构建（`bin\Release\net10.0-windows\` 或重新 publish 的 `win-x64\publish\`）；可用 `GlmBalanceTaskbar.exe --dump-icon` 把各尺寸渲染帧导出到 `%TEMP%\glmbalance-icon\` 排查 |

## 模型中转（relay）

在任务栏程序内内嵌**本机 OpenAI 兼容中转服务**：opencode 等 OpenAI 兼容客户端把 baseURL 指向 `http://127.0.0.1:8787/v1`，请求按渠道优先级转发到智谱 Coding / 自定义中转 等上游，配额耗尽或出错时自动失败转移。

```
opencode (localrelay provider) ──http://127.0.0.1:8787/v1──▶ GlmBalanceTaskbar.exe
    ├─ 原有：AppContext 30s 配额轮询 → 任务栏图标 / 托盘浮窗 / 详情窗口（不改动其语义）
    └─ RelayManager：RelayServer（HttpListener）+ ChannelRouter（优先级 + 三态状态机）
         + 探测（IUsageProbe：zhipu-monitor / none）+ 请求日志 + 配置持久化
```

技术约束：零新 NuGet 依赖（HttpListener/HttpClient 内置）；仅监听本机回环 `127.0.0.1`；`PublishSingleFile` 与"仅 Desktop Runtime"承诺不变。

### 启用方式

**首启引导（推荐）**：配置无 `relay` 段时，详情窗口「模型中转」分组显示「启用中转」开关——点按即自动生成完整 `relay` 段（端口 8787、自动生成 localToken、播种 zhipu-coding / custom-relay 渠道）并启动运行时，无需手工编辑配置文件；随后在管理面板 Provider 池为 custom-relay 填写 API Key 即完成接入。也可随时用同一开关关闭/再启用（运行时启停，不重启程序；关闭前会确认）。

手工方式：在 **exe 同目录** `glmbalance-taskbar.json` 中加入 `relay` 段（该段整体缺省 → 中转关闭，原功能零影响）：

```jsonc
{
  // 原有字段全部保留不动
  "relay": {
    "enabled": true,
    "port": 8787,
    "localToken": "",                    // 空 → 首启生成随机 token（32 字节 hex）写回本文件
    "probeIntervalSeconds": 60,
    "degradeBelowPct": 5,                // 探测剩余 % 低于此值 → degraded（软降级，不参与路由）
    "restoreAbovePct": 10,               // 高于此值（或窗口重置）→ 恢复（滞后防抖）
    "cooldown": { "quotaSeconds": 300, "errorThreshold": 3, "errorSeconds": 60 },
    "providers": [                       // 缺失或空 → 自动播种下面两项并写回
      { "id": "zhipu-coding", "name": "智谱 Coding", "priority": 1, "probe": "zhipu-monitor",
        "baseURL": "https://open.bigmodel.cn/api/coding/paas/v4",
        "apiKey": "",                    // 空 → 复用全局 apiKey 读取链（auth.json 优先 zhipuai-coding-plan）
        "models": ["glm-5.3-flash", "glm-5.3"], "enabled": true },
      { "id": "custom-relay", "name": "自定义中转", "priority": 2, "probe": "none",
        "baseURL": "https://your-relay.example.com/v1",
        "apiKey": "<你的 custom-relay key>",
        "models": ["glm-5.3-flash", "glm-5.3"], "enabled": true }
    ],
    "agents": { "pi": { "configPath": "" } }
  }
}
```

> 注意：配置文件为**严格 JSON**（不支持注释），上例 `jsonc` 注释仅为说明。

### relay 配置字段表

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `relay.enabled` | `true`（段存在时） | 中转总开关；`relay` 段整体缺省 = 关闭 |
| `relay.port` | `8787` | 监听端口（仅 `127.0.0.1`）；改动需重启程序 |
| `relay.localToken` | 空=自动生成 | Bearer 鉴权 token；写回失败 → 警告并**免鉴权运行** |
| `relay.probeIntervalSeconds` | `60` | 每 provider 探测间隔（10–3600 钳制） |
| `relay.degradeBelowPct` / `restoreAbovePct` | `5` / `10` | 探测驱动的软降级阈值（滞后防抖） |
| `relay.cooldown.quotaSeconds` | `300` | 上游 `429/402/403` 硬冷却时长（秒） |
| `relay.cooldown.errorThreshold` / `errorSeconds` | `3` / `60` | 5xx/网络异常连续 N 次冷却 M 秒 |
| `relay.providers[].priority` | — | 升序优先（默认优先，用量还有就优先）；平序按配置数组顺序 |
| `relay.providers[].probe` | `none` | `zhipu-monitor`（quota/limit 探测，优先复用任务栏轮询快照）/ `none`（不可检查，纯反应式） |
| `relay.providers[].apiKey` | 空 | 空 → 复用全局读取链；空 key 的渠道 = `disabled(no-key)` 不参与路由。**第三方（非智谱）渠道必须显式填 key**：全局读取链是智谱定向的，非智谱域渠道留空会被判为 `no-key` 而非静默继承智谱 key |
| `relay.agents.pi.configPath` | 空 | Pi 配置路径（占位预留） |

### 渠道状态机

- **normal**：参与路由。
- **cooldown**（硬，响应驱动）：上游 `429/402/403` → 300s；`5xx`/网络异常连续 ≥3 次 → 60s；成功一次清零计数。时间戳实现，无定时器。
- **degraded**（软，探测驱动）：剩余 % 低于 `degradeBelowPct` → 降级（不参与路由）；高于 `restoreAbovePct` 或窗口重置 → 恢复。
- 探测失败/不可检查 → 不改状态仅记错误；**反应式信号始终兜底**（配额 API 正常但请求仍 429/402/403 → 照进硬冷却）。
- 失败转移仅发生在未向客户端写入任何字节前；429/402/403/5xx/网络异常切下一渠道，4xx 业务错原样回传不转移；全部渠道失败 → `502` 错误汇总。
- 流式（SSE）逐块转发 + flush，idle 120s 无 chunk 才断（断流不重试）。

### HTTP 端点

| 端点 | 鉴权 | 说明 |
| --- | --- | --- |
| `POST /v1/chat/completions` | Bearer localToken | OpenAI 兼容转发（请求体原样透传，不重序列化） |
| `GET /v1/models` | Bearer localToken | 池内 Enabled 渠道 models 并集（OpenAI 格式） |
| `GET /health` | 免 | `{"status":"ok"}` |
| `GET /status` | 免（仅本机回环） | 渠道状态 JSON（state/lastError/lastUsed/冷却剩余/计数/用量%（remainingPct=较小窗口值，另含 fiveHourPct / weekPct 分窗口值）+ current） |
| `GET /admin` | 免 | 管理面板（五 Tab：总览 / Provider 池 / 日志 / 部署 / 设置） |
| `/api/*` | Bearer localToken | 管理 JSON API（provider CRUD / move / test / import、agents deploy、settings、logs；key 只写不读，响应一律掩码） |

### opencode 对接

1. 启动程序后打开管理面板（托盘菜单「管理面板」或 `http://127.0.0.1:8787/admin`），在「部署」Tab 生成预览并写入；或在「Provider 池」Tab 从发现列表核对渠道。
2. 写入效果（`~/.config/opencode/opencode.json`，自动备份 `opencode.json.bak-<时间戳>`，**绝不改动既有 provider 条目**）：

```jsonc
{
  "model": "localrelay/glm-5.3-flash",   // 「设为全局默认」勾选时写入
  "provider": {
    "localrelay": {
      "name": "Local Relay",
      "npm": "@ai-sdk/openai-compatible",
      "options": { "baseURL": "http://127.0.0.1:8787/v1", "apiKey": "<localToken>" },
      "models": { "glm-5.3-flash": { "name": "glm-5.3-flash" }, "glm-5.3": { "name": "glm-5.3" } }
    }
  }
}
```

3. 验证：`opencode run -m localrelay/glm-5.3-flash "回复ok两个字"`。
4. 重置 localToken（设置 Tab）后必须**同步重新部署** opencode 端，否则请求 401。

### 故障排查

| 现象 | 处理 |
| --- | --- |
| 详情窗提示"中转服务启动失败（端口 8787）" | 端口被占用：改 `relay.port` 或释放占用进程；配额显示不受影响 |
| 请求 401 invalid_api_key | localToken 与 opencode 端 localrelay apiKey 不一致：重新部署或同步 token |
| `502 中转失败：所有可用渠道均失败` | 打开 `/status` 看各渠道 lastError；常为上游 key 失效或全部冷却 |
| 渠道一直 normal 但请求 429 | 该渠道 probe=`none`（不可检查）：属预期，反应式兜底会在首个 429 后进冷却 |
| 托盘悬停"中转  无可用渠道" | 所有渠道 disabled / no-key / degraded / cooldown；检查 providers 配置 |
| 管理面板 401 | 首次打开会提示输入 token（exe 同目录 json 的 `relay.localToken`） |

### 红线说明

- apiKey 只写不读：任何 API 响应、`/status`、日志中 key 一律掩码（`sk-…后4位`）；不把明文 key 写进 git 仓库内任何文件。
- 发现/部署功能**绝不删除或修改** opencode.json 中既有 provider 条目（`zhipuai-coding-plan` 与 `custom-relay` 是插件与 relay 取 key 的共同数据源）。
- 配置写回只有一条路径（JsonNode 合并写回 `glmbalance-taskbar.json`，保留文件其他字段）。
- 中转仅监听本机回环；日志与错误汇总不含请求体内容。

## 开发实例（--dev）

带 `--dev` 参数启动即进入**开发实例**：与生产实例并存、互不干扰，上游全部 mock，适合本地测试失败转移/降级/部署流程。

| 维度 | 生产（不带参数） | 开发（`--dev`） |
| --- | --- | --- |
| 配置目录 | exe 同目录 `glmbalance-taskbar.json` | `%LOCALAPPDATA%\GlmBalanceTaskbar-dev\glmbalance-taskbar.json` |
| relay 端口 | 8787（默认） | **8788**（默认；显式写 8787 会被拒绝启动） |
| mock 上游 | —（真实上游） | 进程内 `http://127.0.0.1:8789`（所有渠道 baseURL 与 monitorBase 建池时强制覆写指向它，配置文件保留原值） |
| 单实例标识 | `Local\GlmBalanceTaskbar.SingleInstance` 等 | 加 `.Dev` 后缀（互斥体/窗口事件/AUMID 各自独立，双实例任务栏身份分离） |
| 窗口标题 | `GLM 周 52%` | `[DEV] GLM 周 52%` |
| `/status` | 不含 `dev` 字段 | 携带 `"dev": true` |
| 部署 | 可落盘（自动备份） | **dry-run**：只预览、绝不写盘 |

用法示例：

```sh
# 启动开发实例（托盘出现 [DEV] 图标，与生产实例并存）
GlmBalanceTaskbar.exe --dev

# 离线评估（不打网络：内置 mock 快照，无监听无写盘）
GlmBalanceTaskbar.exe --dev --once

# 对指定目录生成 dev 语义配置（port=8788、渠道播种 mock baseURL + 假 key mock-<id>）
GlmBalanceTaskbar.exe --dev --relay-init <dir>

# 图标诊断（与 mock/配置无关，行为同现状）
GlmBalanceTaskbar.exe --dev --dump-icon
```

dev 配置文件可在生产格式基础上增加顶层 `devMock` 段编程假上游行为（生产实例解析时忽略该键）：

```jsonc
{
  "relay": { "enabled": true, "port": 8788, "providers": [ /* baseURL 填真实地址即可，运行时自动覆写为 mock */ ] },
  "devMock": {
    "upstreamPort": 8789,
    "chatContent": "[mock] dev 假响应",
    "latencyMs": 50,
    "quota": { "fiveHourRemainingPct": 80, "weekRemainingPct": 60 },   // weekRemainingPct < 5 可观察 degraded，改回 > 10 观察 restore
    "providers": { "zhipu-coding": { "chatStatus": 200, "modelsStatus": 200 } }  // 429/500/streamBreak 可观察失败转移
  }
}
```

> 注意：dev 配置为**严格 JSON**；`devMock` 段省略时全部取默认。mock 上游 8789 被占用时 dev relay 拒绝启动（宁可失败不可打真实上游）。设计与验收详见 `docs/plans/dev-prod-separation.md`。

## 隐私

与插件一致：直接从本机请求智谱官方 API，apiKey 仅用于向智谱 API 鉴权，不经过任何第三方。
