# glm-balance (opencode-glm-balance)

一个 [opencode](https://opencode.ai) TUI 插件，在侧栏显示**智谱 GLM Coding Plan** 的用量与配额（支持**团队版**）。

## 显示内容

团队版（积分制）示例：

```
GLM 用量  [pro]
5h 积分   █████████░ 97%
            已用 457/15000   ↻ 09-23 00:23
周积分    █████░░░░░ 52%
            已用 32016/66000  ↻ 周五 14:14
活跃度
  · 累计    3.33G tokens · 139h53m
  · 今日    135M · 峰值 420M @ 09-06
  · 连续    23 天
  · 近14天  ▃▆█▇▁▅▄▃
```

- **5 小时积分窗口**：剩余 % 进度条 + 已用/总量积分 + 重置时间
- **每周积分窗口**：剩余 % 进度条 + 已用/总量积分 + 重置时间（显示星期几）
- **活跃度**（可开关）：累计 tokens 与使用时长、今日消耗、单日峰值、连续使用天数、近 14 天 sparkline（官网"活跃度"面板同源数据，`/api/monitor/credit-usage/activity`，独立 5 分钟刷新）
- 兼容旧版个人版接口：`5h token` 窗口、`周额度`、24h token 总量、分模型明细、MCP 月度、工具调用次数（接口无数据的行自动隐藏）
- 进度条按剩余量着色：绿 > 40% / 黄 15–40% / 红 < 15%
- 账号等级（pro / max 等）

## 工作原理

1. 自动检测已配置的智谱 provider：
   - `options.baseURL` 含 `bigmodel.cn` / `api.z.ai`，**或**
   - provider `id` / `name` 含 `zhipu` / `bigmodel` / `z.ai`。
2. 复用该 provider 的 **apiKey**（仅来自 opencode provider 配置，不读取任何环境变量）。
3. 请求智谱监控 API（非公开文档接口）：
   - `/api/monitor/usage/quota/limit` — 配额窗口与套餐等级
   - `/api/monitor/credit-usage/activity` — 活跃度（累计/峰值/连续天数/每日序列，`type=3`，近一年窗口，独立 5 分钟刷新）
   - `/api/monitor/usage/model-usage` — 24h 模型用量（团队维度无数据，自动隐藏）
   - `/api/monitor/usage/tool-usage` — 24h 工具用量（同上）
4. **团队版**：带 `bigmodel-organization` / `bigmodel-project` 请求头 + `type=2` 查询参数（组织/项目维度）；个人版不带。
5. 窗口类型按响应中的 `unit` 字段精确区分（3=5 小时、6=周、5=月）；新版积分窗口类型为 `CREDIT_LIMIT`，旧版 `TOKENS_LIMIT`/`TIME_LIMIT` 同样兼容；字段缺失时按窗口时长/重置时间排序兜底。
6. 每 **30 秒**刷新一次，并在每次会话回复完成（`session.idle`）时刷新。
7. 接口返回 `success:false` 时，侧栏直接显示服务端错误信息（如 `当前用户不存在coding plan` 提示 type/头配置有误）。

> **隐私**：插件直接从你的机器请求智谱官方 API，不经过任何第三方；apiKey 仅用于向智谱 API 鉴权。

## 要求

- opencode **≥ 1.17.0**（基于 Bun，运行时自动转译 TSX，无需构建步骤）
- 已配置一个智谱 provider（baseURL 指向 `https://open.bigmodel.cn/api/anthropic` 等）

## 安装（本地文件方式）

1. 在 `~/.config/opencode/package.json` 中加入依赖（opencode 启动时会自动 `bun install`）：

```json
{
  "dependencies": {
    "@opencode-ai/plugin": "^1.18.0",
    "solid-js": "^1.9.12",
    "@opentui/solid": "^0.4.5"
  }
}
```

2. 在 `~/.config/opencode/tui.json` 的 `plugin` 数组里引用本项目的入口文件。

**个人版**：

```json
{
  "$schema": "https://opencode.ai/tui.json",
  "plugin": ["D:/AlisL/Source/Repos/glm-balance/src/index.tsx"]
}
```

**团队版**（元组形式传选项，组织/项目 ID 从官网用量统计页面的请求头 `bigmodel-organization` / `bigmodel-project` 获取）：

```json
{
  "$schema": "https://opencode.ai/tui.json",
  "plugin": [
    [
      "D:/AlisL/Source/Repos/glm-balance/src/index.tsx",
      {
        "organization": "org-xxxxxxxx",
        "project": "proj_xxxxxxxx",
        "activity": true
      }
    ]
  ]
}
```

可配置项：

| 选项 | 说明 | 默认 |
| --- | --- | --- |
| `organization` | 团队版组织 ID（`bigmodel-organization` 请求头） | 不带 |
| `project` | 团队版项目 ID（`bigmodel-project` 请求头） | 不带 |
| `usageType` | 用量维度：`2`=团队、`1`=个人 | 有 organization 时为 `2`，否则不带 |
| `monitorBase` | 监控 API 基地址覆盖 | 从 provider baseURL 推导 |
| `activity` | 活跃度数据开关：`false` 时不请求也不显示 | `true` |
| `activityType` | 活跃度查询 `type` 参数 | `3` |
| `activityBase` | 活跃度接口基地址覆盖 | 同 `monitorBase` |

3. 重启 opencode，侧栏即出现（仅在有会话视图时显示，首页无侧栏）。

### 发布为 npm 包后

`package.json` 已配置 `exports: { "./tui": "./src/index.tsx" }`，发布后可直接在 `tui.json` 中写 `"plugin": ["opencode-glm-balance"]`。

## 自定义

编辑 `src/index.tsx`：

| 项目 | 位置 |
| --- | --- |
| 刷新间隔 | `setInterval(..., 30000)`（毫秒） |
| 着色阈值 | `colorFor()` |
| 进度条宽度 | `BarRow` 的 `width` |
| 重置时间字段候选 | `RESET_KEYS` |
| 窗口单位映射 | `UNIT_SECONDS`（3=小时 4=天 5=月 6=周） |

## 网络 / 代理

插件用原生 `fetch`，自动遵循 `HTTPS_PROXY` / `HTTP_PROXY` 环境变量。

## License

[MIT](./LICENSE) © AlisL
