# GlmBalanceTaskbar（任务栏常驻）

把智谱 GLM Coding Plan 的**周积分剩余 %** 实时显示在 Windows 11 任务栏上的 C# 小程序，与 opencode 插件 [glm-balance](../README.md) 同源数据。

## 效果

- 应用以最小化状态常驻任务栏，图标实时渲染剩余百分比（如 `52%`），按剩余量着色：绿 > 40% / 黄 15–40% / 红 < 15%
- 任务栏按钮叠加**原生进度条**显示剩余比例
- 悬停任务栏图标显示摘要（窗口标题即 tooltip）：`GLM 周 52% · 已用 32016/66000 · ↻ 周五 14:14`
- 点击图标弹出**详情窗口**：5h 积分 / 周积分 / MCP 月度进度条与重置时间、24h tokens、分模型明细、活跃度（累计 / 今日 / 连续天数 / 近 14 天用量柱状图）
- 关闭窗口 = 最小化常驻，退出走窗口内"退出"按钮；重复启动会恢复已有窗口（单实例）

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

1. 运行 `GlmBalanceTaskbar.exe` → 程序最小化常驻，任务栏出现用量图标
2. 右键任务栏图标 → **固定到任务栏**（保留槽位；未运行时显示静态图标）
3. 开机自启：`Win+R` 输入 `shell:startup` → 把 exe 快捷方式放入启动文件夹

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
| 图标数字显示不全/异常 | 确认运行的是最新构建（`bin\Release\net10.0-windows\` 或重新 publish 的 `win-x64\publish\`）；可用 `GlmBalanceTaskbar.exe --dump-icon` 把各尺寸渲染帧导出到 `%TEMP%\glmbalance-icon\` 排查 |

## 隐私

与插件一致：直接从本机请求智谱官方 API，apiKey 仅用于向智谱 API 鉴权，不经过任何第三方。
