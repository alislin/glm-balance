# 通道控制增强设计：手动强制切换 + 拖拽调优先级（v2）

> 编排契约：glm-balance-chan-ctl / planner **轮次 2**（评审 R1 修订版）
> v1 原稿保留：`docs/plans/channel-control.md`（轮次 1）——本文件即原稿就地替换
> 评审：`11 评审 R1.md` —— 0 blocker / 3 major / 10 minor，结论 REVISE；本文全部落实。
> 评审 R2：`12 评审 R2.md` —— **APPROVED**（13/13 落实真实；新发现 3 minor n1~n3，开发阶段一并落实）。
> 状态：**APPROVED / 已实现**（writer 轮次 1，按本文全量落地；R2 n1~n3 一并处理）
> 需求：① 用户手动强制切换通道；② 拖拽调整通道优先级。
> 红线：不干扰生产实例（PID 3160，端口 8787）；不触碰 8788/8789（另一编排正在跑运行时验收）。

---

## 修订摘要表（R1 → v2）

| 项 | 处置 | 落点 |
|----|------|------|
| M1 | **落实**：reorder 分支移至 HandleProvidersAsync 前部、404 前置（L129-132）**之前**，与 import（L82）同级；声明 "reorder" 为渠道 id 保留字 | §4.1 AdminApi ②、§4.2 |
| M2 | **落实**（取评审推荐 a）：池卡片 pin 态数据源 = `loadProviders()` 并行 `GET /api/overview` 取 `forced.id`，前端缓存 `_forcedId`；零后端改动 | §4.1 admin.html、§5.1 |
| M3 | **落实**：新增风险 R9（转义）；新代码一律 escapeHtml + 事件委托（data-action/data-id）规避内联 handler 单引号上下文；顺手修复现状 `pv-id` 未转义与 `moveProvider('${p.id}')` 注入点 | §4.1 admin.html、§6 R9 |
| m1 | **落实**：PreviewLine pin 分支优先，pin 生效时跳过既有 preferred≠current 解释分支 | §4.1 RelayManager ② |
| m2 | **落实**：仅 dragend 收口（drop 仅 preventDefault）；容器级 dragover 兜底末尾插入 | §5.2 |
| m3 | **落实**：A4 前置补"apiKey = mock-\<id\>（relay-init dev 播种）"前提 | §7 前置、A4 |
| m4 | **落实**：U7 观测手段明确：响应体 `[mock]` 前缀即证出站必经 mock；辅助 netstat 无外连 | §7 U7 |
| m5 | **落实**：SetForced 返回 false（极小池重建竞态）→ 404，与前置语义一致 | §4.1 AdminApi ①、§4.2 |
| m6 | **落实**：新增风险 R10 一句话闭合（不加机制） | §6 R10 |
| m7 | **落实**：A9 补空池/空数组边界语义 | §7 A9 |
| m8 | **落实**：注明 no-key 渠道行走引导文案不走 RelayRowText，无 [强制] 前缀属预期 | §5.3 |
| m9 | **落实**：补"日志 chain 零改动"声明 | §4.1、§7 A2 |
| m10 | **落实**：R3 措辞扩展覆盖 exclude 二跳瞬时窗口 | §6 R3 |

---

## 1. 目标 / 非目标

### 目标

| # | 目标 | 落点 |
|---|------|------|
| G1 | 用户可把流量**固定路由到指定渠道**，直到手动解除 | ChannelRouter pin 状态 + AdminApi pin 端点 + admin 面板/托盘控件 |
| G2 | 强制状态**全局可见**（/status、/api/overview、admin 总览与池卡片、托盘详情窗、悬停行） | RelayStatus 快照 + 各 UI 渲染 |
| G3 | admin 面板 Provider 池支持**拖拽重排**，结果写回配置文件并即时生效 | admin.html HTML5 DnD + reorder 端点 |
| G4 | 优先级显式化：拖拽后 Priority 重编号 1..n 并持久化 | MutateProviders + PersistAndApply 既有路径 |
| G5 | dev 模式下两功能照常可用（出站恒打 mock 上游，无新增风险） | 复用 BuildPool dev 覆写与 DevMock 规则 |

### 非目标

- **每模型维度强制**（pin 到"某渠道+某模型"组合）——v1 只做全局单槽 pin。
- **多个强制通道 / 强制通道队列**——单槽，后 pin 替换前 pin。
- **强制状态持久化到配置文件**——运行时意图，重启即失效（理由见 §2）。
- **自动解除策略**（如"强制通道失败 N 次自动解除"）——v1 仅手动解除；冷却让位机制已兜底可用性。
- **托盘 WinForms 拖拽调序**——不做（理由见 §3.5）。
- **移动端 / 触屏拖拽**——无诉求，桌面浏览器 HTML5 DnD 足够。

---

## 2. 语义决策：强制切换是什么

### 推荐：**sticky pin（固定路由直到解除），不可用时让位自动规则 + 全局可见提示**

确切语义：

1. 用户对渠道 X 执行"设为强制"→ 运行时产生一个**单槽 pin**（`forcedId = X`）。
2. 此后每次选路（`Select(model, exclude)`）：若 X 存在于池中、**未被本轮 exclude**（即首跳）、
   支持该模型、且 `IsRoutable`（Enabled ∧ 有 key ∧ 非 cooldown/degraded/no-key）→ **直接选 X**；
   否则**完全回退到既有自动规则**（Priority 升序取第一），请求不失败。
3. X 的失败转移语义不变：强制通道首跳失败后进入 exclude，链上后续跳按自动规则走
   （pin 只影响首选，不改变 RelayServer 既有 failover 链行为）。
4. 用户再次对别的渠道执行"设为强制"→ 替换 pin；执行"解除强制"→ 清空，回到纯自动。
5. 删除被 pin 的渠道 → pin 自动清除（配置操作不可留下悬空意图）。
6. **重启后 pin 消失**（不写配置文件）。

### 备选方案与不取理由

| 备选 | 内容 | 不取理由 |
|------|------|----------|
| B1 "立即切过去，之后仍按自动规则" | 仅立刻把 current 换到 X，下一请求起按 Priority 正常选 | 几乎等价于"把 X 的 Priority 改到 1"（G3 的拖拽已覆盖该诉求）；语义与效果脱节，用户会困惑"切了为什么又切回来了" |
| B3 "sticky 且顶住" | 冷却/降级也强制打 X，全部失败才报错 | 强制通道 429 冷却 300s 期间，全部请求都撞墙 5 分钟——与"负载均衡兜底"的产品定位冲突；可用性让位于确定性，应由**可见提示**补足，而不是硬顶 |

### 状态存放：RelayManager/ChannelRouter **运行时状态**，不进 ProviderConfig

- "强制"是**运行时意图**，不是渠道属性：它不随 `PUT /api/providers/{id}` 编辑、不写
  `glmbalance-taskbar.json`、不走 `ToJsonNode/Persist`。放进 ProviderConfig 需要在
  **两处 CloneConfig**（RelayManager.cs L110-120、AdminApi.cs L462-472）、`ToJsonNode`、
  `ApplyProviderPatch` 同步维护一个非配置字段，污染面大且易漏。
- 存放在 `ChannelRouter`（`_forcedId`，与 `_pool` 同一把 `_gate` 锁）：`ReplacePool` 时做
  存在性校验（防悬空），池重建（`ApplyOptions`）、启停、探测调度都天然不影响它——
  pin 按 id 存，不挂在 ProviderRuntime 上，无需 `Adopt` 迁移。
- 重启即失效是**有意语义**：pin 是"这一会儿我要 X"的会话级操作（类比"暂停"），持久化会让
  重启后的流量悄悄固定在一个配置已不再优先的渠道上，违反最小惊讶。

### 与既有机制的交互矩阵（A.4 的回答）

| 场景 | 行为 |
|------|------|
| 强制通道进入冷却（429/402/403/连续错误） | **让位**：`IsRoutable=false`，请求按自动规则走；UI 显示"强制通道 X 冷却 120s，已按自动规则走 Y"；冷却到期惰性归位后 pin 自动恢复生效（无定时器，复用 `IsRoutable` 的惰性归位） |
| 强制通道 degraded（用量低于阈值/无 key） | 同上让位 + 提示 |
| 强制通道被停用（Enabled=false） | 让位 + "已停用"提示；重新启用后 pin 恢复生效（pin 不因停用清除） |
| 强制通道首跳失败（非冷却类，如 5xx 单次） | 既有 failover 链接手（exclude），下一请求仍先试 X——pin 不改变链语义；**该次请求期间 forced.active 仍=true，属预期**（pin=首选偏好，当次转移经日志 chain `X → Y` 可见，见 §6 R3） |
| 用户再次手动切换 | 替换 pin（单槽） |
| 用户解除强制 | 回到纯自动 |
| 与"启用/停用"组合 | 见上；与"优先级调整"组合：拖拽/↑↓ 改 Priority 不影响 pin（pin 优先于 Priority） |
| 与导入/编辑组合 | 均不影响 pin（按 id 存） |
| 删除被 pin 渠道 | AdminApi DELETE 成功路径顺手 `ClearForced(id)`；`ReplacePool` 另有防御性校验 |
| /status 与 admin 展示 | 见 §5 |

### dev 模式

dev 实例下 pin/reorder 照常可用，零额外风险：`BuildPool` 的 dev 克隆覆写保证出站恒打
mock 上游（8789）；验收可经 DevMock 既有规则（`mock-<id>` Bearer key → `chatStatus=429/5xx`）
精确制造"强制通道冷却"场景（见 §7；前提 m3：渠道 key 须为 mock-\<id\> 形态）。

---

## 3. 方案选项与取舍

### 3.1 强制切换语义

见 §2（推荐 sticky pin；备选 B1/B3 已列）。

### 3.2 强制状态的表达与 API 形态

| 选项 | 内容 | 取舍 |
|------|------|------|
| **A（推荐）** | `ChannelRouter` 运行时字段 + 专用端点 `POST /api/providers/{id}/pin` / `DELETE /api/providers/{id}/pin` | 语义正交：pin 不混入配置 CRUD；RESTful 幂等；与 MutateProviders 无耦合（不写文件，无需 PersistAndApply） |
| B | `POST /api/providers/{id}/switch { mode: "pin" \| "auto" }`（契约提示的形态） | 单端点两动作，body 分支语义；不如 pin/unpin 直白，且 DELETE 已有天然语义 |
| C | ProviderConfig 加 `Forced` 字段并持久化 | 配置里出现非配置意图；重启后悬空风险；CloneConfig×2/ToJsonNode/Patch 全要跟着改。**否决** |

接线：pin **不经** `MutateProviders`（不动 providers 配置），直接调 `_mgr.Router.SetForced/ClearForced`；
`ForceReevaluate` 也**不需要**——pin 不改任何渠道状态，下一次 `Select` 自然生效。

### 3.3 pin 的作用面（A.2 的回答：托盘 DetailForm 做不做）

| 选项 | 内容 | 评估 |
|------|------|------|
| **A（推荐）** | admin 面板全量支持 + 托盘 DetailForm **右键渠道行**菜单（"强制路由到此渠道 / 解除强制"） | 成本低：DetailForm 已有事件外派模式（`RelayToggleRequested` 等），加一个 `PinRequested(id, pin)` 事件 + ContextMenuStrip（菜单列出全部渠道，无需逐行命中测试）≈ 40 行；收益高：托盘是常驻主 UI，渠道异常时用户第一反应在托盘 |
| B | 仅 admin 面板，托盘只读展示 | 省约 40 行，但托盘用户要开浏览器才能解除强制，体验断裂 |

### 3.4 拖拽 API 形态（B.2 的回答）

| 选项 | 内容 | 取舍 |
|------|------|------|
| **A（推荐）** | 新增 `POST /api/providers/reorder`，body `{ ids: [全量新顺序] }` | ① 拖拽结果天然是"全量新顺序"，一对一映射，**幂等**（连拖几次、双开管理页 last-write-wins 都是合法语义）；② 一次 MutateProviders + 一次 PersistAndApply，单次写盘；③ 校验简单（ids 必须是现有渠道全排列）；④ 不污染既有 move 的 `dir` 语义 |
| B | 扩展 move 端点支持 `toIndex` | 一个端点两种 body 语义（`dir` vs `toIndex`），分支耦合；只表达"移动一个"，跨多格拖拽等价于一次 reorder 但要多算目标序；无增益 |

### 3.5 Priority 值策略与托盘拖拽

- **Priority 重编号 1..n（推荐）**：拖拽落定后按新顺序赋 `Priority = index+1`。与
  `NextPriority()`（import/PUT 新建取 `max+1`）完全兼容（max=n，导入渠道排尾）；
  与 move 的相邻交换兼容（重编号后 Priority 两两不同，交换仍正确）。间隙策略（如 ×10）
  仅在"高频插入中间位置"时有价值，本场景池规模 ≤ 几个渠道，纯增复杂度。
- **托盘 WinForms 拖拽：不做**。DetailForm 渠道行是**只读 Label 流式布局**（非 ListView），
  支持拖拽需引入命中测试、拖拽光标、重排动画与窗口重排联动，改动面大；而拖拽是低频
  精调操作，admin 面板（常驻可达）已覆盖。托盘对 B 的参与 = 展示新 Priority/顺序（随
  快照自动刷新，零改动）。

### 3.6 拖拽交互实现

| 选项 | 内容 | 取舍 |
|------|------|------|
| **A（推荐）** | HTML5 原生 DnD + **live 重排预览**（dragover 时实时移动被拖卡片到插入点） | 桌面浏览器全支持；live 预览即"所见即所得"，无需额外放置指示线；drop 时 DOM 顺序即最终顺序，回滚只需按快照重渲染 |
| B | HTML5 DnD + 占位指示线（placeholder element） | 需维护指示线的插入位置计算与 CSS，视觉上反而不如 live 重排直观 |
| C | 按钮模拟拖拽（选中 + ↑↓） | 已有 ↑↓，无新价值 |

---

## 4. 涉及模块与接口

### 4.1 文件级改动清单

| 文件 | 改动 |
|------|------|
| `taskbar/GlmBalanceTaskbar/Relay/ChannelRouter.cs` | ① `_forcedId` 字段（与 `_pool` 同 `_gate` 锁）；② `SetForced(id)`（池内存在才置位，返回 bool）、`ClearForced(id?)`、`ForcedId` 属性；③ `Select()`：锁内先取 pin 候选（池内 ∧ 不在 exclude ∧ `Supports(model)` ∧ `IsRoutable(now)`）命中即返回，否则走既有 Priority 规则；④ `Current()`：同理先取 pin（不带模型维度）；⑤ `ReplacePool()`：置池后若 `_forcedId` 不在新池 → 清空（防御，正常路径由 AdminApi DELETE 清） |
| `taskbar/GlmBalanceTaskbar/Relay/RelayStatus.cs` | 新增 `public sealed record ForcedRouteStatus(string Id, bool Active, string? Reason);`；`RelayStatus` record **尾部**追加 `ForcedRouteStatus? Forced = null`，带 `[property: JsonIgnore(Condition = WhenWritingDefault)]`（与既有 `Dev` 字段同一模式：null → 序列化省略，无 pin 时生产 /status 响应逐字节不变）。`ProviderStatus` **不改**（UI 用 `Forced.Id` 比对） |
| `taskbar/GlmBalanceTaskbar/Relay/RelayManager.cs` | ① `Snapshot()`：组装 `Forced`（`_router.ForcedId` → 池内找 rt → `Active = rt.Config.Enabled && rt.ResolvedKey != null && Inspect(now).State == Normal`；`Reason` = 不可用时给中文短因，如"冷却 120s"/"已停用"/"degraded：剩余 3%"）；② `PreviewLine()`：**pin 分支优先（m1）**——pin 存在且 active → 直接返回 `中转  [强制] {id} · {状态}`，**跳过既有 preferred≠current 解释分支**（不与之叠加）；pin 存在但未 active → `中转  {current.Id}（强制通道 {forcedId} {reason}，已按自动规则）`；无 pin → 现状逻辑逐字不变。**n1：pin 分支置于既有 current==null 短路之后** |
| `taskbar/GlmBalanceTaskbar/Relay/AdminApi.cs` | ① `HandleProvidersAsync` 的 `switch (segments[3])` 增 `case "pin"`：`POST` → `SetForced(rt.Id)`，**返回 false（极小竞态：池在查出后被重建）→ 404 `{"error":"渠道不存在"}`（m5，与前置语义一致）**；成功 200 `{ok, forcedId}`；`DELETE` → `ClearForced(rt.Id)` → 200 `{ok:true}`；② **reorder 分支（M1）置于 HandleProvidersAsync 前部、404 前置（L129-132）之前，与 import 分支（L82）同级**：`segments.Length==3 && segments[2]=="reorder" && POST` → 读 `ids` 数组 → 全排列校验（数量相等、无重复、无未知 id；**空池+空数组 → 200 空操作，非空池+空数组 → 400**，m7）→ `MutateProviders`（按 ids 重排 list 并 `Priority=i+1`）→ `PersistAndApply(ReplaceProviders)` → 200/500。**"reorder" 成为渠道 id 保留字**：池内渠道 id 恰为 "reorder" 时，`POST /api/providers/reorder` 语义被 reorder 端点覆盖（现状该路径为 404 not_found，无既有功能破坏；对该渠道的 CRUD 走 `PUT/GET/DELETE /api/providers/reorder` 不受影响）；③ DELETE 渠道成功路径：`if (rt.Id == _mgr.Router.ForcedId) _mgr.Router.ClearForced(rt.Id);`。**不改** move / PUT / import / ProviderToJson；**日志 chain 零改动（m9）**：RequestLog.Chain 天然记录 pin 首位，admin 日志页 `[A → B]` 渲染现状即可用 |
| `taskbar/GlmBalanceTaskbar/Relay/RelayServer.cs` | **无改动**（`/status` 序列化 Snapshot 自动携带 forced；`/api/*` 已统一路由进 AdminApi） |
| `taskbar/GlmBalanceTaskbar/Relay/admin.html` | ① **pin 态数据源（M2）**：`loadProviders()` 改为并行 `Promise.all([api("/api/providers"), api("/api/overview")])`，取 `ov.forced?.id` 存入前端变量 `_forcedId`（呼应 §5.1"状态语义单一来源"；池 tab 无轮询，pin 操作成功后的 `loadProviders()` 即刷新按钮态）；② Provider 池卡片：按 `_forcedId` 渲染"设为强制/取消强制"按钮与 📌 徽标（`.badge.forced`，accent 色）；③ 总览卡片："← 当前通道"旁加"📌 强制通道"徽标；pin 未生效时总览顶部横幅（复用 nokey 横幅模式）：`强制通道 X 当前不可用（{reason}），已按自动规则走 Y — [解除强制]`，**forcedId 与 reason 拼 innerHTML 前一律 escapeHtml（M3）**；④ **拖拽（m2）**：`#pv-table` 内 `.pv-card` 全部 `draggable=true`；`dragstart`（记录拖起 id、`.dragging` 半透明）→ `dragover`（preventDefault 必调；按鼠标 Y 与卡片中线比较实时 insertBefore，**容器级 dragover 兜底：鼠标越过最后卡中线 → appendChild 至末尾**）→ **仅 dragend 收口读 DOM 顺序得 ids（drop 仅 preventDefault，避免双重 POST）**，与拖前快照一致则跳过；否则 POST reorder；成功 `loadProviders()`；失败按快照重渲染 + 错误提示；↑↓ 按钮保留并存；⑤ **转义（M3）**：池卡片操作按钮**改事件委托**（`#pv-table` 容器监听 click，button 携带 `data-action`/`data-id`，规避内联 `onclick='${p.id}'` 单引号 JS 字符串注入上下文；pin/移动等全部按钮统一迁移）；**顺手修复现状注入点**：`pv-id` 徽标 `${p.id}` → `${escapeHtml(p.id)}`（L378） |
| `taskbar/GlmBalanceTaskbar/DetailForm.cs` | ① `RelayRowText(...)` 增参 `forcedId`：被 pin 行文本加 `[强制] ` 前缀（替代/叠加 ▶）；**缺 key 渠道行不走 RelayRowText（走引导文案），无 [强制] 前缀属预期（m8，总览横幅仍可见）**；② `_lblRelayRows` 各行 `MouseClick` 右键 → `ContextMenuStrip`：每渠道一项"强制路由到 {id}"（当前 pin 项打勾）+ 分隔 + "解除强制"（无 pin 时禁用）；③ 新事件 `PinRequested(string providerId, bool pin)` |
| `taskbar/GlmBalanceTaskbar/AppContext.cs` | 订阅 `PinRequested` → `relay.Router.SetForced/ClearForced` → 立即 `_form.UpdateRelay(relay.Snapshot())` 刷新（≈10 行） |
| 配置文件 | **不新增任何字段**；reorder 经既有 `ReplaceProviders` 写回 providers（顺序 + 重编号后的 priority） |

### 4.2 API 契约

鉴权：与全部 `/api/*` 一致，Bearer localToken，无 token → 401 `{"error":"unauthorized"}`。

#### `POST /api/providers/{id}/pin` — 设为强制通道

- 请求体：无（空 body 合法）。
- `200` → `{"ok":true,"forcedId":"zhipu-coding"}`（幂等：重复 pin 同一渠道同响应）。
- `404` → `{"error":"渠道不存在"}`：id 不在池（前置检查），**或 SetForced 因池重建竞态返回
  false（m5，与前置同语义）**。
- 说明：不写文件、不重建池、不调 ForceReevaluate；下一次请求选路即生效。

#### `DELETE /api/providers/{id}/pin` — 解除强制

- `200` → `{"ok":true,"forcedId":null}`（幂等：本就无 pin 或 pin 的不是该 id 也返回 200；
  pin 指向其他 id 时此调用**不**清除——解除必须显式针对被 pin 渠道或用面板按钮）。
- `404` → `{"error":"渠道不存在"}`。

#### `POST /api/providers/reorder` — 全量重排优先级

- **实现位置（M1）**：分支置于 HandleProvidersAsync 前部、404 前置（AdminApi.cs L129-132）
  **之前**，与 import 分支（L82）同级——若放在 `switch (segments[3])` 的自然位置则永不可达
  （池内无 id="reorder" 的渠道，前置即 404）。
- 请求体：`{"ids":["a","b","c"]}`——**全部**渠道 id 的新顺序。
- 校验失败 → `400` `{"error":"ids 必须恰好包含全部渠道 id（无重复、无缺失）"}`。
- **边界（m7）**：空池 + `{"ids":[]}` → `200` 空操作（全排列校验 0==0 通过）；非空池 +
  `{"ids":[]}` → 400。
- `200` → `{"ok":true,"warnings":[...]}`（warnings 为 PersistAndApply 写回告警，通常空）。
- `500` → `{"ok":false,"warnings":[...]}`（写盘失败；内存已变，下次成功写回收敛——与既有
  move/PUT 同级语义）。
- 副作用：providers 列表重排 + `Priority` 重编号 1..n + 写回配置 + `ApplyOptions` 重建池。
- **保留字声明（M1）**："reorder" 成为渠道 id 保留字（见 §4.1 AdminApi ②）。

#### `GET /status`（含 `/api/overview`）响应新增

```jsonc
{
  // ...既有字段不变...
  "forced": {                       // 仅在存在 pin 时出现（WhenWritingDefault 省略 null）
    "id": "zhipu-coding",
    "active": true,                 // 当前是否可路由（false = 让位自动规则中）
    "reason": null                  // active=false 时的中文短因；active=true 为 null
  }
}
```

无 pin 时响应与现状**逐字节一致**（与 `Dev` 字段同一 `JsonIgnoreCondition.WhenWritingDefault`
模式）；有 pin 时为**增量可选字段**，既有消费者（opencode 端）不感知。

---

## 5. 交互设计

### 5.1 admin 面板 — 强制切换

- **Provider 池 tab**：每张 `pv-card` 的操作行新增按钮，按钮态由 `_forcedId`（M2：并行
  `/api/overview` 取得）渲染：
  - 未 pin（`_forcedId == null` 或不指向本卡）：`设为强制` → POST pin → 刷新，卡片头部出现
    📌 徽标。
  - 已 pin 该卡（`_forcedId === p.id`）：按钮变 `取消强制` → DELETE pin。
  - 已 pin 其他卡：该卡按钮仍为 `设为强制`（点击即替换 pin）。
- **总览 tab**（5s 轮询）：
  - 当前通道 KPI 与卡片：pin 生效时该卡片显示 `📌 强制通道` 徽标（与 `← 当前通道` 并列）。
  - pin 未生效（冷却/降级/停用）：顶部横幅 `📌 强制通道 X 当前不可用（冷却 120s），已按自动规则走 Y`，
    附 `解除强制` 按钮——确保"为什么没走我指定的渠道"有明确答案。横幅文案中的 forcedId
    与 reason 一律 escapeHtml（M3）。
- **状态语义单一来源**：`/api/overview` 的 `forced` 字段；UI 不自行推断。

### 5.2 admin 面板 — 拖拽完整交互流

1. 进入 Provider 池 tab，`loadProviders()` 渲染卡片列表（并行拉取 pin 态，M2），同时把当前
   顺序存入 `_pvOrder` 快照。
2. 用户按住某卡片拖动：`dragstart` 置 `.dragging`（opacity .5）+ `dataTransfer` 记 id。
3. 拖动经过其他卡片：`dragover`（preventDefault 必调，否则不允许 drop）按鼠标 Y 与目标卡片
   中线比较，**实时 insertBefore** 被拖卡片——列表即时重排即预览，无额外指示线；
   **容器级 dragover 兜底（m2）：鼠标越过最后一张卡中线后 insertBefore null / appendChild，
   使卡片可插入列表末尾**（否则末位不可达）。
4. 松手：**仅 `dragend` 收口（m2）**——读取容器 DOM 顺序 → ids 数组（`drop` 仅 preventDefault
   允许放置，不做收口，避免 drop+dragend 连续触发导致双重 POST）。
   - 与 `_pvOrder` 相同 → 无操作（纯晃了一下）。
   - 不同 → POST `/api/providers/reorder`：
     - 成功 → `loadProviders()` 重渲染（优先级徽标变为重编号后的 1..n）。
     - 失败（400/500/断网）→ 按 `_pvOrder` 快照**重渲染恢复原顺序** + `pv-msg` 错误提示。
5. 拖拽期间 ↑↓ 按钮仍可用（两者都收敛到同一全量顺序）；无需拖拽把手（整卡可拖，卡片内
   按钮点击经事件委托处理，click 与 dragstart 天然区分）。
6. **ESC 取消（n3）**：`dragstart` 后按 ESC 取消拖拽时（HTML5 DnD 的 dragend 在 ESC 取消时
   也会触发，用 drop 是否发生或 escaped 标志区分），列表恢复 `_pvOrder` 快照顺序、
   **不触发 POST**。

### 5.3 托盘 DetailForm — 做什么 / 不做什么

- **做**：① 被强制渠道行加 `[强制]` 前缀（高亮沿用当前通道样式）；② 渠道行**右键** →
  上下文菜单（逐渠道"强制路由到 X"（当前 pin 打勾）/ "解除强制"）；③ 悬停托盘的
  PreviewLine 显示 `[强制]` 或回退说明（见 §4.1 RelayManager 行）。
- **不做**：拖拽调序（§3.5）；行内嵌按钮（Label 布局塞按钮破坏流式重排，右键菜单已覆盖）。
- **已知边界（m8）**：缺 key 渠道行走引导文案、不经 `RelayRowText`，故被 pin 的 no-key
  渠道行**无 [强制] 前缀**——属预期，强制状态经总览横幅与 `forced.active=false` 仍可见；
  验收 U6 不将其判为缺陷。

---

## 6. 风险

| # | 风险 | 评估与缓解 |
|---|------|-----------|
| R1 | **并发 MutateProviders 写回交错**：reorder/import/PUT/move 都是"锁内 mutate → 锁外 PersistAndApply"两段式，理论上交错可互相覆盖 providers 写回 | 与现状 move/import **完全同级**（既有既定语义）；操作频率低（人工 UI）；全部写操作已串行过 `_providersLock`。接受，不新增锁（避免锁序风险） |
| R2 | **拖拽与自动选路竞态**：拖拽重排瞬间是否有请求选到"半新半旧"优先级 | 无：池替换是原子引用切换（`ReplacePool`），in-flight 请求持旧引用不受影响，新请求读新池；Priority 在 MutateProviders 锁内一次性重编号，无中间态暴露 |
| R3 | **强制态与冷却交互**：用户误以为"强制 = 一定走该渠道"，冷却期间看到请求走别处产生困惑 | 让位是设计决策而非 bug；靠 §5.1 的总览横幅 + 托盘 PreviewLine 的"已按自动规则"明示兜底；文档/文案统一用"强制通道（不可用时回退自动规则）"表述。**覆盖 exclude 二跳瞬时窗口（m10）**：pin 渠道首跳失败未达冷却阈值时，当次请求走 Y 而 forced.active 仍=true——语义正确（pin=首选偏好，非单请求保证），日志 chain `X → Y` 即"强制通道当次失败转移"的可视证据；文档措辞与 R3 同源覆盖 |
| R4 | **pin 与池重建**：`ApplyOptions`/启停/探测热更新会不会丢 pin | pin 存在 `ChannelRouter` 字段而非 ProviderRuntime，`ReplacePool` 不触碰；仅当 pin 的 id 从池中消失（被删除）才清除——双保险（AdminApi DELETE 显式清 + ReplacePool 防御清） |
| R5 | **/status 形状变更影响既有消费者** | 无 pin 时省略 `forced` 字段（WhenWritingDefault），生产无 pin 场景逐字节不变；有 pin 时为增量可选字段，opencode 端不读未知字段 |
| R6 | **dev/prod 行为一致性** | 两功能全部走同一代码路径，无 dev 分支；dev 差异仅既有 BuildPool 覆写（出站 mock）与配置根（`%LOCALAPPDATA%\GlmBalanceTaskbar-dev\`）；验收场景（冷却让位）在 dev 用 DevMock 规则注入，生产等价场景为真实 429 |
| R7 | **双开管理页并发拖拽**：A 页拖完 B 页再拖（基于过期顺序） | 全量 reorder + 全排列校验：若期间渠道增删 → 400 → B 页回滚提示；若仅顺序过期 → last-write-wins（用户意图明确时正确） |
| R8 | **不触碰 8788/8789 与生产 8787** | 本设计纯代码与文档改动；验收（§7）只用 dev 实例自身端口语义（dev relay 默认 8788、mock 8789 为**既有拓扑**，不新增监听、不改端口），所有操作针对"运行中的 dev 实例自己"；生产实例 PID 3160 零交互 |
| R9 | **XSS/注入（M3）**：新代码放大 admin.html 现有未转义缺陷——现状 `pv-id`（L378）直接拼 `${p.id}`（id 用户可控任意文本）、`moveProvider('${p.id}')` 处于内联单引号 JS 字符串上下文（id 含 `'` 可逃逸执行）；新增面更大：pin 内联 handler、总览横幅拼 forcedId+reason（reason 含探测返回的 SourceLabel） | **新代码一律 escapeHtml**（admin.html L370 已有）+ **事件委托**（容器监听 click + `data-action`/`data-id`，彻底规避内联 handler 的引号逃逸上下文，pin/移动/编辑等按钮统一迁移）；**顺手修复现状两处注入点**（pv-id 转义、按钮迁委托）；托盘 WinForms 侧 `Label.Text` 非 HTML 上下文，无此问题 |
| R10 | **多 admin 页 pin 状态不同步（m6）**：池 tab 无轮询 → B 页按钮态过期 | 低风险：pin 同 id 幂等 200、pin 另一卡 = 替换 pin，均为合法语义；操作后 `loadProviders()` 即刷新本页；不加同步机制 |

---

## 7. 验收标准（可操作；全部可在 dev 实例验证）

前置：`--dev` 实例运行（relay 127.0.0.1:8788，mock 上游 127.0.0.1:8789，种子渠道 ≥2，
记为 A=priority 1、B=priority 2）；管理 token 就绪。所有 curl 带 `-H "Authorization: Bearer $TOKEN"`。
**DevMock 注入前提（m3）**：渠道 A/B 的 apiKey 须为 `mock-A`/`mock-B` 形态（`--relay-init`
dev 播种默认）；若渠道保留真实显式 key，MockUpstreamServer 按 Bearer 反查 miss → 恒 200、
注入失效——验收前先确认 key 形态。

### API 级

| # | 操作 | 预期 |
|---|------|------|
| A1 | `POST /api/providers/A/pin` | 200 `{"ok":true,"forcedId":"A"}`；`GET /status` 出现 `forced.id="A", active=true` |
| A2 | pin A 后 `POST /v1/chat/completions`（model 两渠道均支持） | `/api/logs` 该请求 chain 首位为 A（强制优先于 Priority）。**观测点零改动（m9）**：RequestLog.Chain 既有字段天然覆盖，admin 日志页 `[A → B]` 渲染现状即用 |
| A3 | `DELETE /api/providers/A/pin` | 200；`/status` 的 `forced` 字段消失；下一请求 chain 首位回到 A（A 仍 Priority 1）；再把 A/B 优先级对调后验证回自动 |
| A4 | pin A 后经 DevMock 规则令 A 返回 429（`mock-A` key `chatStatus:429`，**前提见 m3**），再发请求 | A 进入 cooldown；请求**不失败**，chain 为 `B`；`/status` `forced.active=false` 且 `reason` 含"冷却"；mock 恢复 200、冷却到期后再发请求 → chain 首位回到 A（pin 自动恢复生效） |
| A5 | `POST /api/providers/不存在/pin` | 404 |
| A6 | pin A 后 `PUT /api/providers/A`（改 name）、`POST .../move`、import 均正常 | pin 不受影响（`/status` forced 仍在） |
| A7 | pin A 后 `DELETE /api/providers/A` | 200；`/status` forced 消失（删除连带清 pin） |
| A8 | `POST /api/providers/reorder {"ids":["B","A"]}` | 200；`GET /api/providers` 显示 B.priority=1、A.priority=2；dev 配置文件 providers 数组顺序与 priority 同步；下一请求 chain 首位为 B |
| A9 | `POST /api/providers/reorder {"ids":["B"]}` / `{"ids":["B","B"]}` / `{"ids":["B","X"]}` | 400，池与配置不变。**边界（m7）**：空池 + `{"ids":[]}` → 200 空操作；非空池 + `{"ids":[]}` → 400 |
| A10 | `GET /status`（无 pin 时） | 响应不含 `forced` 字段（与改动前逐字节一致） |

### UI 级（admin 面板，浏览器开 dev 实例 `/admin`）

| # | 操作 | 预期 |
|---|------|------|
| U1 | Provider 池卡片点"设为强制" | 按钮变"取消强制"，卡片出现 📌 徽标；总览对应卡片出现"📌 强制通道"（池 tab pin 态来自并行 overview，M2） |
| U2 | A4 场景进行中看总览 | 顶部横幅"强制通道 A 当前不可用（冷却 Ns），已按自动规则走 B"+ 解除按钮；点解除 → 横幅消失、徽标消失 |
| U3 | 拖 B 卡片到列表顶部松手 | live 预览正确；**拖到列表末位（越过最后卡中线）也能插入（m2 容器兜底）**；落定后顺序更新、优先级徽标变 1/2；刷新页面顺序保持（已持久化） |
| U4 | 拖拽中断网模拟失败（DevTools 改 token 或断网） | 列表回滚拖前顺序 + 错误提示，无半态；网络面板确认仅一次 reorder POST（dragend 单点收口，m2） |
| U5 | ↑↓ 按钮与拖拽混用 | 结果一致、无冲突 |
| U6 | 托盘 DetailForm：pin 后 | 被强制行出现 `[强制]` 前缀（**no-key 渠道行走引导文案无此前缀，属预期，m8**）；右键渠道行 → 菜单可切换/解除；悬停托盘行显示 `中转  [强制] A · 正常` |
| U7 | 全程 dev | 观测手段（**m4**）：① 客户端响应体带 `[mock]` 前缀即证出站必经 mock（MockUpstreamServer 响应特征）；② 辅助 `netstat -ano | findstr <dev-pid>` 运行期无公网外连；8787 生产实例无任何变化 |
| U8 | 注入面抽查（M3） | 渠道 id 含 `'` 与 `<img>` 等字符（经 PUT 设置）→ 池 tab 渲染为纯文本、按钮事件正常（事件委托）；总览横幅 reason 中特殊字符不逃逸 |

### 回归

- 无 pin、无拖拽时的选路、failover、冷却、降级、import、move、PUT/DELETE 行为与改动前一致
  （重点回归：`/status` 形状 A10、move 相邻交换在重编号后仍正确、pv-id 转义修复不影响正常 id 展示）。
