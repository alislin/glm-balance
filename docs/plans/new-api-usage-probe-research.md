# New API 用量探测可行性调研报告（New API 网关渠道）

- 调研人：`newapi-researcher`（轮次 1，纯静态调研）
- 日期：2026-11-20
- 结论速览：**能接**。推荐 probe 类型 `new-api`，用 `GET {站点根}/api/usage/token/` + 现有 sk- key（`Authorization: Bearer …`），`RemainingPct = total_available / total_granted × 100`，`ResetAt` 不可得（置 null）。
- 上下文：目标渠道为任意 New API 网关实例（baseURL 形如 `https://<your-instance>/v1`），背后是开源网关 New API（GitHub `QuantumNous/new-api`，原 `Calcium-Ion/new-api`，one-api 分支）。本仓库探测框架为 `IUsageProbe` / `ProbeResult`（`taskbar/GlmBalanceTaskbar/Relay/IUsageProbe.cs`），现有实现样例 `Relay/Probes/ZhipuMonitorProbe.cs`。

> 调研合规声明：本次仅访问 GitHub 公开源码 / 官方文档与 PR 记录，**未对任何目标实例发出任何请求**，未读取仓库配置中的真实 apiKey。

---

## 1. 接口清单（与"用量/额度/quota"相关）

quota 内部单位说明：New API 所有 `quota` 字段是整数计费单位，`QuotaPerUnit = 500000` 对应 $1（`common/constants.go`：`var QuotaPerUnit = 500 * 1000.0 // $0.002 / 1K tokens`）。算**百分比**时分子分母同单位，无需换算。

### 1.1 `GET /api/usage/token/` —— 令牌维度用量查询 ⭐ 首选

- 路由：`router/api-router.go`（`usageRoute := apiRouter.Group("/usage")` → `tokenUsageRoute := usageRoute.Group("/token")` → `tokenUsageRoute.GET("/", controller.GetTokenUsage)`）
- 中间件：`CORS + CriticalRateLimit + TokenAuthReadOnly`（见 §4）
- 处理器：`controller.GetTokenUsage`（`controller/token.go`）
- 认证：`Authorization: Bearer sk-xxxx`（带不带 `sk-` 前缀均可，服务端自动剥）
- 响应（HTTP 200）：

```json
{
  "code": true,
  "message": "ok",
  "data": {
    "object": "token_usage",
    "name": "令牌名称",
    "total_granted": 1000000,        // 授予总量 = remain_quota + used_quota
    "total_used": 12345,             // 已用 = used_quota
    "total_available": 987655,       // 剩余 = remain_quota
    "unlimited_quota": false,        // 是否无限额度
    "model_limits": {...},           // 模型白名单（map）
    "model_limits_enabled": false,
    "expires_at": 0,                 // 令牌过期时间（秒级 Unix；-1 归一化为 0=永不过期）
    "user_quota": {                  // 仅当令牌开启 allow_user_quota_query 时返回
      "total_available": 0,          // 所属用户剩余额度
      "total_used": 0                // 所属用户已用额度
    }
  }
}
```

- 关键语义（源码注释原文）："**此只读鉴权允许已过期或余额耗尽的令牌查询**，仍检查停用状态、IP 限制和用户状态"——正好覆盖探测最关心的"额度用尽"时刻。
- 版本门槛：由 **PR #1161**（`feat: query usage of token`）引入，**2025-08-23 合入 main**。更早部署的实例没有该端点（404）。
- 路径尾斜杠：路由注册在 group `/api/usage/token` 下的 `"/"`，**实际路径带尾斜杠** `/api/usage/token/`；不带斜杠会被 gin `RedirectTrailingSlash` 301 重定向。客户端建议直接带尾斜杠。
- 来源字段对应（`model/token.go` 的 `Token` struct）：`remain_quota` / `used_quota` / `unlimited_quota` / `expired_time`。

### 1.2 `GET /dashboard/billing/subscription`（含 `/v1/dashboard/billing/subscription`）

- 路由：`router/dashboard.go`（组前缀 `/`，中间件 `RouteTag("old_api") + GlobalAPIRateLimit + CORS + TokenAuth`）：
  ```go
  apiRouter.GET("/dashboard/billing/subscription", controller.GetSubscription)
  apiRouter.GET("/v1/dashboard/billing/subscription", controller.GetSubscription)
  apiRouter.GET("/dashboard/billing/usage", controller.GetUsage)
  apiRouter.GET("/v1/dashboard/billing/usage", controller.GetUsage)
  ```
- 处理器：`controller/billing.go` 的 `GetSubscription`
- 认证：**sk- key（`TokenAuth`）**——但注意 `TokenAuth` 走 `model.ValidateUserToken`：令牌**已过期或已耗尽会直接 401**，拿不到"剩 0%"读数。
- 响应（OpenAI 老 billing 兼容格式）：

```json
{
  "object": "billing_subscription",
  "has_payment_method": true,
  "hard_limit_usd": 12.0,            // = (remain_quota + used_quota) / QuotaPerUnit
  "soft_limit_usd": 12.0,
  "system_hard_limit_usd": 12.0,
  "access_until": 0                  // 令牌过期时间（秒）；unlimited_quota 时 amount 固定 100000000
}
```

- **语义受运营开关影响**：`DisplayTokenStatEnabled`（默认 `true`，`common/constants.go`）为 `true` 时返回**当前令牌**的额度；为 `false` 时返回**所属用户**的总额度（`GetUserQuota/GetUserUsedQuota`）。开关值由站点运营控制，客户端不可知。
- 保留情况确认：New API **保留**了 one-api 系的这组 billing 接口（独立注册在 `router/dashboard.go`，不在 `relay-router.go`）。

### 1.3 `GET /dashboard/billing/usage`（含 `/v1/dashboard/billing/usage`）

- 处理器：`controller/billing.go` 的 `GetUsage`
- 响应：

```json
{ "object": "list", "total_usage": 246.9 }   // = used_quota / QuotaPerUnit * 100（OpenAI“美分”语义）
```

- 与 1.2 组合可算剩余百分比：
  `剩余% = (1 - (total_usage / 100) / hard_limit_usd) × 100`
- 同样受 `DisplayTokenStatEnabled` 开关影响（token 维度 ↔ user 维度漂移）；`unlimited_quota` 时 `hard_limit_usd` 固定 1 亿，百分比无意义。

### 1.4 `GET /api/user/self` —— 用户自身额度

- 路由：`router/api-router.go`（`selfRoute := userRoute.Group("/")`，中间件 `DisableCache + UserAuth`）
- 认证：**面板登录 session cookie 或面板 access token**（新版为带 scope 的 PAT，`middleware/auth.go` 的 `classifyDashboardCredential`；旧版为 32 位 access token + `New-Api-User: <用户ID>` 头）。**普通 sk- 调用令牌不能调**（`TokenOrUserAuth` 明确"Opaque credentials are relay API keys here, never dashboard PATs"，sk- 值会被送进 `TokenAuth` 而非面板认证）。
- 响应：`{success, message, data: User}`，User JSON 含 `quota`（剩余）、`used_quota`（已用）、`request_count`、`group` 等（`model/user.go` 的 `User` struct；`GetSelfUserById` 显式 Select 这些列）。
- 这是**账号（user）维度**：账号下所有令牌共享的总额度，不区分单个 key。

### 1.5 其它相关（参考）

| 接口 | 认证 | 说明 |
|---|---|---|
| `GET /api/log/token` | sk- key（`TokenAuthReadOnly`） | 该 key 的调用日志（分页明细），可看消耗记录但不给出剩余额度，不适合做探测主数据源 |
| `GET /api/token/`、`GET /api/token/:id` | 面板 `UserAuth`（session/PAT） | 令牌管理列表，`remain_quota`/`used_quota` 字段同源；需要面板凭证，sk- key 不可用 |
| `GET /api/data/self` | 面板 `UserAuth` | 用户额度时间序列统计，面板画图用，非探测场景 |

### 1.6 不存在的概念

- New API **没有** 5h 窗口 / 周窗口等"分时间窗口配额"概念：额度是纯余额池模型（充值/赠送进 `quota`，消费扣减），窗口化用量统计只有面板图表（`/api/data/*`）。
- 因此本仓库 `ProbeResult.FiveHourPct / WeekPct / ResetAt` 对 New API 渠道恒为 null（详见 §3）。

---

## 2. 数据视角：全站 vs 认证身份维度

| 接口 | 返回视角 | 满足"这一个 key 还剩多少"？ |
|---|---|---|
| `/api/usage/token/` | **当前 Bearer 令牌**（token_id 由 key 反查） | ✅ 精确命中：`total_available` 就是这个 key 的剩余 |
| `/dashboard/billing/subscription` + `/usage` | 运营开关决定：`DisplayTokenStatEnabled=true` → 当前令牌维度；`=false` → 所属**用户**维度 | ⚠️ 默认部署（true）下满足；开关被改则漂移为账号级 |
| `/api/user/self` | 认证用户（账号）维度 | ⚠️ 只有账号级总额度，单 key 无独立额度时等价，否则偏粗 |

- 无"全站统计"暴露问题：三个候选接口都只返回认证身份自己的数据，探测不会看到其他用户/全站信息。

---

## 3. 与 ProbeResult 的映射

`ProbeResult(Available, RemainingPct, ResetAt, Error, SourceLabel, FiveHourPct, WeekPct)`，探测主方案为 `/api/usage/token/`：

| 字段 | 映射 | 说明 |
|---|---|---|
| `Available` | HTTP 200 → `true`；401/403/网络错 → `false`（`Error`=状态码+响应 message） | 401=令牌无效/已禁用/IP 白名单不符；403=所属用户被封 |
| `RemainingPct` | `unlimited_quota==false && total_granted>0` 时 `round(total_available / total_granted * 100)`，钳制 0..100 | 两字段同单位（quota 整数），无需 QuotaPerUnit 换算 |
| `RemainingPct`（不可算时） | `unlimited_quota==true` 或 `total_granted<=0` → `null` | 无限额度没有百分比语义，退化为纯反应式（`Available` 仍有效） |
| `ResetAt` | **恒 `null`** | 唯一的时间字段 `expires_at` 是令牌过期时间（绝大多数部署为 0=永不过期），不是"窗口重置时间"，语义不符不能用 |
| `SourceLabel` | 建议 `"额度"`（或 `"New API 额度"`） | 余额池模型，无窗口名 |
| `FiveHourPct` / `WeekPct` | **恒 `null`** | New API 无 5h/周窗口概念（§1.6），如实说明 |

billing 接口（若做 fallback）映射：`RemainingPct = (1 - total_usage/100 ÷ hard_limit_usd) × 100`；`hard_limit_usd ≥ 100000000` 视为无限额度 → `null`。同样 `ResetAt=null`（`access_until` 是过期时间）。

---

## 4. 认证可行性（目标渠道手里只有 sk- key）

| 接口 | 凭 sk- key 可调？ | 依据 |
|---|---|---|
| `/api/usage/token/` | ✅ **可以** | `TokenAuthReadOnly`：仅验证 key 存在（`GetTokenByKey`）+ 未被禁用 + IP 白名单 + 所属用户未封禁；**明确放行已过期/已耗尽令牌**（源码注释原文见 §1.1） |
| `/dashboard/billing/*` | ✅ 可以，但带限制 | `TokenAuth` → `ValidateUserToken`：要求令牌当前有效；**耗尽/过期的令牌直接 401**——探测恰好在"用尽"时刻拿不到读数，只能得到不可用信号 |
| `/api/user/self` | ❌ 不可以 | 需要 session cookie 或面板 access token（新版 PAT / 旧版 32 位令牌 + `New-Api-User` 头） |

- **结论：现有 `ProviderConfig.ApiKey`（sk-…）直接复用即可，无需新增任何配置字段**；目标渠道把 `probe` 从 `none` 改为 `new-api` 即完成接入配置。
- 若未来想要账号级视角（user_quota 之外更全的信息），可选：① 令牌开 `allow_user_quota_query`（运营侧操作，`/api/usage/token/` 响应会多出 `user_quota` 子对象，**新建令牌默认开、存量令牌升级后默认关**）；② 面板生成 access token 并在本仓库 `ProviderConfig` 新增可选字段——这是配置面变化，**首版不建议**。

---

## 5. 风险

1. **实例版本不确定（最大风险）**：`/api/usage/token/` 2025-08-23 才合入（PR #1161）。目标实例部署版本未知，且契约禁止对实例发请求验证。若实例过旧 → 404。缓解：探测实现把 404 视为"实例不支持"，`Error` 写明原因并建议改回 `probe=none`；billing 接口（one-api 时代即有，兼容面极广）可作为二期 fallback。
2. **billing 语义漂移**：`DisplayTokenStatEnabled` 由运营控制，无法从客户端探测其取值；为 `false` 时 billing 返回账号级额度。百分比仍可算但语义变了。首选方案 `/api/usage/token/` 不受此开关影响。
3. **限流**：`/api/usage/token/` 挂 `CriticalRateLimit`（`middleware/rate-limit.go`：按**客户端 IP** 固定窗口计数；`CriticalRateLimitEnable` 默认 `false` 即不启用；启用后默认 `20 次 / 20 分钟`，可被运营改）。另有全局 `GlobalAPIRateLimit`（同样默认可关）。本仓库探测间隔 `probeIntervalSeconds` 默认 60s、钳制 ≥10s，即使限流开启也远低于 20 次/20 分钟。收到 429 时读取 `Retry-After` 退避、本轮记探测失败即可。
4. **向网关暴露额外查询**：每次探测多一次轻量 GET；不消耗 quota、不计费、不写用量日志表；副作用仅为令牌 `accessed_time`/缓存刷新与网关访问日志。频率跟随现有探测间隔即可，无需额外节流。
5. **返回值不含总额度的兜底**：`unlimited_quota=true`（无总额）→ `RemainingPct=null` 纯反应式；`total_granted=0` → 同上；401 时无法区分"key 失效"与"额度耗尽"——二者对调度语义等价（都应触发冷却/降级），按 `Available=false` 处理。
6. **开源版本差异 / 实例自定义**：实例可能为 fork 或魔改版（含反改、CDN、WAF）。字段解析按"宽松取数"实现：仅依赖 `data.total_granted / total_used / total_available / unlimited_quota / expires_at` 这几个自 PR #1161 起就稳定的核心字段，缺字段时降级为 `Available=true, RemainingPct=null`。

---

## 6. 结论与建议

**结论：能接。**（首版即可落地，无需新增配置字段；唯一未证实项是目标实例版本，属运行时一次探测即可验证的事，不阻塞动工。）

建议的探测方式：

1. **probe 类型名**：`new-api`（注册进 `ProbeRegistry`：`"new-api" => new NewApiProbe(ctx)`，`taskbar/GlmBalanceTaskbar/Relay/IUsageProbe.cs` L43–47 的 switch 加一行）。
2. **新实现文件**：`Relay/Probes/NewApiProbe.cs`，模式照抄 `ZhipuMonitorProbe.cs`（无快照复用需求，每次直接请求）。
3. **请求**：
   - URL：把 `ProviderConfig.BaseUrl`（形如 `https://<your-instance>/v1`）去掉 `/v1` 尾段取站点根，拼 `/api/usage/token/`（**带尾斜杠**）。
   - 头：`Authorization: Bearer <ApiKey>`（`NonEmpty(p.ApiKey)`，空则直接 `Available=false`，与 ZhipuMonitorProbe 同样的 key 判空逻辑；不回落全局读取链——那是智谱定向的，参考 `RelayManager.cs` H1-F2/M1 规则）。
   - 超时建议 10–15s，跟随现有 HTTP 客户端配置。
4. **响应映射**（§3 表）：`200` → 按 `total_available/total_granted` 算 `RemainingPct`；`404` → `Error="实例不支持 /api/usage/token（需 2025-08 后的 New API，PR #1161）"`；`401/403` → `Available=false`；`429` → 按 `Retry-After` 退避、本轮失败；其它 5xx/网络错 → `Available=false`。
5. **不做的事**（首版）：不接 billing fallback、不接 `/api/user/self`、不引入 access token 配置字段；`ResetAt/FiveHourPct/WeekPct` 恒 null。
6. **文档落地物**：渠道 `probe` 字段说明从 `zhipu-monitor / none` 扩为 `zhipu-monitor / new-api / none`（`RelayOptions.cs` L13 注释与 `DetailForm`/`Discovery` 的建议类型提示同步，`OpenCodeConfigSource.cs` 对 `new-api` 域名可建议 `new-api` 探测类型——属实现期细节，此处仅提示有这两处联动点）。

实现所需信息是否齐全：**齐全**。端点路径、认证方式、响应字段、限流行为均有源码 + 官方文档双重佐证；缺的仅是"目标实例版本"这一个运行时事实，实现后第一次探测即可判定（404 → 回退 `none`）。

---

## 参考来源

GitHub 源码（`QuantumNous/new-api`，main 分支， Calcium-Ion 为旧组织名重定向）：

1. `router/api-router.go` —— `/api/usage/token/`、`/api/user/self`、`/api/log/token` 路由与中间件链
   https://github.com/QuantumNous/new-api/blob/main/router/api-router.go
2. `router/dashboard.go` —— billing 四条路由（`/dashboard/billing/subscription`、`/v1/dashboard/billing/subscription`、`/dashboard/billing/usage`、`/v1/dashboard/billing/usage`）+ `TokenAuth`
   https://github.com/QuantumNous/new-api/blob/main/router/dashboard.go
3. `router/relay-router.go` —— 反证：relay 侧无 billing 路由
   https://github.com/QuantumNous/new-api/blob/main/router/relay-router.go
4. `middleware/auth.go` —— `TokenAuthReadOnly`（放行已过期/耗尽令牌的只读查询）、`TokenAuth`（严格校验）、`UserAuth`/`classifyDashboardCredential`（session/PAT）
   https://github.com/QuantumNous/new-api/blob/main/middleware/auth.go
5. `middleware/rate-limit.go` —— `CriticalRateLimit` 按 ClientIP 固定窗口
   https://github.com/QuantumNous/new-api/blob/main/middleware/rate-limit.go
6. `controller/token.go` —— `GetTokenUsage`（响应字段全量）、`GetTokenStatus`；`AddToken` 中 `allow_user_quota_query` 默认值逻辑
   https://github.com/QuantumNous/new-api/blob/main/controller/token.go
7. `controller/billing.go` —— `GetSubscription`/`GetUsage` 的 `DisplayTokenStatEnabled` 分支、`QuotaPerUnit` 换算、unlimited→100000000
   https://github.com/QuantumNous/new-api/blob/main/controller/billing.go
8. `model/token.go` —— `Token` struct（`remain_quota`/`used_quota`/`unlimited_quota`/`expired_time=-1`）、`ValidateUserToken`、`GetTokenByKey`
   https://github.com/QuantumNous/new-api/blob/main/model/token.go
9. `model/user.go` —— `User` struct（`quota`/`used_quota`/`request_count`）、`GetSelfUserById`
   https://github.com/QuantumNous/new-api/blob/main/model/user.go
10. `common/constants.go` —— `QuotaPerUnit=500000`、`DisplayTokenStatEnabled=true`、`CriticalRateLimitNum=20 / Duration=20min / Enable=false`
    https://github.com/QuantumNous/new-api/blob/main/common/constants.go

PR / 文档：

11. PR #1161 "feat: query usage of token"（2025-06-05 创建，2025-08-23 合入）—— `/api/usage/token/` 引入点
    https://github.com/QuantumNous/new-api/pull/1161
12. 官方文档：令牌用量查询（`GET /api/usage/token`，含请求/响应示例与字段说明）
    https://doc.newapi.pro/api/token-usage/
13. 官方文档站（新站）同名页：获取令牌使用情况
    https://docs.newapi.ai/zh/docs/api/management/token-management/usage-token-get
14. New API 文档仓库源文件 `docs/api/token-usage.md`
    https://github.com/QuantumNous/new-api-docs/blob/main/docs/api/token-usage.md
15. Apifox 官方接口描述（标注"源码：router/api-router.go:304；处理函数：controller.GetTokenUsage；此只读鉴权允许已过期或余额耗尽的令牌查询"）
    https://apifox.newapi.ai/384083920e0

本仓库代码（只读，未改动）：

16. `taskbar/GlmBalanceTaskbar/Relay/IUsageProbe.cs` —— `ProbeResult` 结构与 `ProbeRegistry` 工厂
17. `taskbar/GlmBalanceTaskbar/Relay/Probes/ZhipuMonitorProbe.cs` —— 探测实现模式参照
18. `taskbar/GlmBalanceTaskbar/Relay/RelayOptions.cs` —— `ProviderConfig`（Probe/BaseUrl/ApiKey 字段）、custom-relay 渠道播种、探测间隔钳制
19. `taskbar/GlmBalanceTaskbar/Relay/RelayManager.cs` —— key 读取链规则（H1-F2/M1）
