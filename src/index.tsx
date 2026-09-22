/** @jsxImportSource @opentui/solid */
import { For, Show, createSignal, type JSX } from "solid-js"
import type { TuiPlugin } from "@opencode-ai/plugin/tui"

// ---------- 类型 ----------

type ProviderLike = {
  id?: string
  name?: string
  key?: string
  options?: { baseURL?: string; apiKey?: string; [key: string]: unknown }
}

type LimitInfo = {
  /** 行标签，如 "5h 积分" / "周额度" */
  label: string | undefined
  /** 剩余百分比 0-100 */
  remaining: number | undefined
  /** 数值明细，如 "457/15000" */
  detail: string | undefined
  /** 明细语义：已用 / 剩余 */
  detailLabel: string | undefined
  /** 重置时间，如 "14:00" / "周三 00:00" */
  resetLabel: string | undefined
}

type ModelUsage = { name: string; tokens: number }
type ToolUsage = { name: string; count: number }

type ActivityInfo = {
  totalTokens?: number
  durationLabel?: string
  currentStreakDays?: number
  longestStreakDays?: number
  todayTokens?: number
  peakTokens?: number
  peakDate?: string
  spark?: string
  fetchedAt: number
}

type UsageInfo = {
  fiveHour?: LimitInfo
  week?: LimitInfo
  mcp?: LimitInfo
  level?: string
  tokens24h?: number
  models?: ModelUsage[]
  tools?: ToolUsage[]
  activity?: ActivityInfo
  error?: string
  fetchedAt: number
}

type GlmInfo = { matched: boolean; baseURL?: string }

/** quota/limit 响应里的 limits[] 条目（非公开接口，字段做宽松处理） */
type RawLimit = {
  /** CREDIT_LIMIT=积分窗口（新版），TOKENS_LIMIT=token 窗口（旧版），TIME_LIMIT=MCP 月度（旧版） */
  type?: string
  /** 已用百分比 */
  percentage?: number
  /** 总量（积分或次数） */
  usage?: number
  /** 已用量（CREDIT_LIMIT） */
  currentValue?: number
  /** 剩余量 */
  remaining?: number
  /** 窗口单位：3=小时 4=天 5=月 6=周 */
  unit?: number
  /** 单位数量：unit=3/number=5 即 5 小时窗口 */
  number?: number
  [key: string]: unknown
}

type PluginOptions = {
  /** 团队版组织 ID（bigmodel-organization 请求头） */
  organization?: string
  /** 团队版项目 ID（bigmodel-project 请求头） */
  project?: string
  /** 用量维度：2=团队 1=个人；默认有 organization 时为 2，否则不带 */
  usageType?: number
  /** 监控 API 基地址覆盖（默认从 provider baseURL 推导） */
  monitorBase?: string
  /** 活跃度数据开关（默认开启；false 时不请求也不显示） */
  activity?: boolean
  /** 活跃度查询维度 type 参数（默认 3） */
  activityType?: number
  /** 活跃度接口基地址覆盖（默认同 monitorBase） */
  activityBase?: string
}

type TuiApi = {
  client?: {
    app?: {
      log?: (args: {
        body: { service: string; level: string; message: string; extra?: Record<string, unknown> }
      }) => Promise<unknown>
    }
  }
  event?: { on?: (name: string, fn: (...args: unknown[]) => void) => (() => void) | undefined }
  lifecycle?: { onDispose?: (fn: () => void) => void }
  slots?: {
    register?: (opts: { order?: number; slots: Record<string, () => JSX.Element> }) => void
  }
  theme?: { current?: any }
  state?: { provider?: ProviderLike[] }
}

// ---------- Provider 检测 ----------

const GLM_HOST_RE = /(open\.bigmodel\.cn|dev\.bigmodel\.cn|bigmodel\.cn|api\.z\.ai)/i
const GLM_ID_RE = /zhipu|bigmodel|z\.ai/i

function getProviders(api: TuiApi): ProviderLike[] {
  return (api?.state?.provider ?? []) as ProviderLike[]
}

function monitorBaseFrom(baseURL: string): string {
  try {
    const u = new URL(baseURL)
    return `${u.protocol}//${u.host}`
  } catch {
    return baseURL.replace(/\/api\/.*$/i, "")
  }
}

function inspectProvider(p: ProviderLike | undefined): GlmInfo {
  if (!p) return { matched: false }
  const bu = p?.options?.baseURL
  if (typeof bu === "string" && GLM_HOST_RE.test(bu)) return { matched: true, baseURL: bu }
  const idName = `${p?.id ?? ""} ${p?.name ?? ""}`
  if (GLM_ID_RE.test(idName)) {
    const base =
      typeof bu === "string" && bu
        ? bu
        : /z\.ai/i.test(idName)
          ? "https://api.z.ai/api/anthropic"
          : "https://open.bigmodel.cn/api/anthropic"
    return { matched: true, baseURL: base }
  }
  return { matched: false }
}

function findGlmProvider(api: TuiApi): ProviderLike | undefined {
  return getProviders(api).find((p) => inspectProvider(p).matched)
}

function providerToken(p: ProviderLike | undefined): string | undefined {
  return (p?.options?.apiKey as string) ?? p?.key
}

// ---------- 时间处理 ----------

const RESET_KEYS = [
  "resetTime",
  "refreshTime",
  "nextResetTime",
  "resetAt",
  "refreshAt",
  "endTime",
  "windowEnd",
  "expireTime",
  "expiryTime",
  "nextWindowTime",
  "resetTimestamp",
  "refreshTimestamp",
  "windowEndTime",
]

const WEEKDAY_NAMES = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"]

function parseTimeMs(value: unknown): number | undefined {
  if (value == null) return undefined
  if (typeof value === "number" && Number.isFinite(value) && value > 0) {
    return value > 1e12 ? value : value * 1000
  }
  if (typeof value === "string") {
    const trimmed = value.trim()
    if (!trimmed) return undefined
    const n = Number(trimmed)
    if (!Number.isNaN(n) && n > 0) return n > 1e12 ? n : n * 1000
    const t = Date.parse(trimmed)
    if (!Number.isNaN(t)) return t
  }
  return undefined
}

/** 今天显示 HH:mm；周窗口显示 周X HH:mm；其他显示 MM-DD HH:mm */
function formatTimeValue(value: unknown, weekly = false): string | undefined {
  const ms = parseTimeMs(value)
  if (ms === undefined) {
    const raw = String(value ?? "").trim()
    return raw || undefined
  }
  const d = new Date(ms)
  if (Number.isNaN(d.getTime())) return undefined
  const pad = (x: number) => String(x).padStart(2, "0")
  const hm = `${pad(d.getHours())}:${pad(d.getMinutes())}`
  const now = new Date()
  if (weekly) return `${WEEKDAY_NAMES[d.getDay()]} ${hm}`
  if (d.toDateString() === now.toDateString()) return hm
  return `${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${hm}`
}

function extractResetLabel(item: Record<string, unknown>, data: Record<string, unknown>, weekly: boolean): string | undefined {
  for (const source of [item, data, (item["usageDetails"] as Record<string, unknown>) ?? {}]) {
    for (const key of RESET_KEYS) {
      if (source[key] != null) {
        const label = formatTimeValue(source[key], weekly)
        if (label) return label
      }
    }
  }
  return undefined
}

// ---------- 配额窗口解析 ----------

const UNIT_SECONDS: Record<number, number> = { 3: 3600, 4: 86400, 5: 2629800, 6: 604800 }
const HOUR_UNIT = 3
const WEEK_UNIT = 6

function toLimitInfo(l: RawLimit, data: Record<string, unknown>): LimitInfo | undefined {
  const used = typeof l.percentage === "number" ? l.percentage : undefined
  const remaining = used != null ? Math.max(0, Math.min(100, 100 - used)) : undefined
  const credit = typeof l.currentValue === "number" && typeof l.usage === "number"
  const detail = credit
    ? `${l.currentValue}/${l.usage}`
    : typeof l.remaining === "number" && typeof l.usage === "number"
      ? `${l.remaining}/${l.usage}`
      : undefined
  const detailLabel = credit ? "已用" : detail != null ? "剩余" : undefined
  const isCredit = l.type === "CREDIT_LIMIT"
  const resetLabel = extractResetLabel(l, data, l.unit === WEEK_UNIT)
  if (remaining == null && detail == null && resetLabel == null) return undefined
  const label =
    l.unit === HOUR_UNIT
      ? isCredit
        ? "5h 积分"
        : "5h token"
      : l.unit === WEEK_UNIT
        ? isCredit
          ? "周积分"
          : "周额度"
        : undefined
  return { label, remaining, detail, detailLabel, resetLabel }
}

type TokenWindow = {
  limit: LimitInfo
  durationSec: number | null
  unit: number | undefined
  resetMs: number | null
  index: number
}

/** 未知窗口时长时，按重置时间距离排序兜底（5h 窗口总是重置更近） */
function windowRank(w: TokenWindow, now: number): number {
  if (w.durationSec != null) return w.durationSec
  if (w.resetMs != null) return Math.max(0, (w.resetMs - now) / 1000)
  return Number.MAX_SAFE_INTEGER
}

export function parseQuotaLimits(payload: any, now = Date.now()): { fiveHour?: LimitInfo; week?: LimitInfo; mcp?: LimitInfo } {
  const data: Record<string, unknown> = payload?.data ?? payload ?? {}
  const limits: RawLimit[] = Array.isArray(data["limits"]) ? (data["limits"] as RawLimit[]) : []

  const tokenWindows: TokenWindow[] = []
  let mcp: LimitInfo | undefined

  limits.forEach((raw, index) => {
    if (!raw || typeof raw.type !== "string") return
    if (raw.type === "TOKENS_LIMIT" || raw.type === "CREDIT_LIMIT") {
      const limit = toLimitInfo(raw, data)
      if (!limit) return
      const durationSec =
        typeof raw.unit === "number" && typeof raw.number === "number" && raw.number > 0 && UNIT_SECONDS[raw.unit]
          ? UNIT_SECONDS[raw.unit] * raw.number
          : null
      let resetMs: number | null = null
      for (const key of RESET_KEYS) {
        const ms = parseTimeMs(raw[key])
        if (ms != null) {
          resetMs = ms
          break
        }
      }
      tokenWindows.push({ limit, durationSec, unit: raw.unit, resetMs, index })
    } else if (raw.type === "TIME_LIMIT") {
      const limit = toLimitInfo(raw, data)
      if (limit) mcp = limit
    }
  })

  // 精确按 unit 区分：3=5 小时窗口，6=周额度
  let fiveHour = tokenWindows.find((w) => w.unit === HOUR_UNIT)?.limit
  let week = tokenWindows.find((w) => w.unit === WEEK_UNIT)?.limit

  // unit 缺失时按窗口时长（或重置时间远近）排序兜底：最短→5h，次短→周
  if (fiveHour == null && week == null && tokenWindows.length > 0) {
    const ranked = [...tokenWindows].sort(
      (a, b) => windowRank(a, now) - windowRank(b, now) || a.index - b.index,
    )
    fiveHour = ranked[0]?.limit
    week = ranked[1]?.limit
  }

  return { fiveHour, week, mcp }
}

// ---------- 活跃度解析 ----------

const SPARK_CHARS = ["▁", "▂", "▃", "▄", "▅", "▆", "▇", "█"]

function sparkline(values: number[], width = 14): string | undefined {
  const slice = values.slice(-width)
  if (slice.length === 0) return undefined
  const max = Math.max(...slice)
  if (max <= 0) return undefined
  return slice.map((v) => SPARK_CHARS[Math.min(7, Math.floor((v / max) * 7.999))]).join("")
}

function fmtDuration(ms: number): string {
  const totalMin = Math.floor(ms / 60000)
  const h = Math.floor(totalMin / 60)
  const m = totalMin % 60
  return m > 0 ? `${h}h${String(m).padStart(2, "0")}m` : `${h}h`
}

function shortDate(date: unknown): string | undefined {
  if (typeof date !== "string" || date.length < 10) return undefined
  return date.slice(5)
}

export function parseActivity(payload: any, now = Date.now()): ActivityInfo | undefined {
  const data = payload?.data
  if (!data || typeof data !== "object") return undefined
  const s = data.summary
  if (!s || typeof s !== "object") return undefined
  const series = Array.isArray(data.series) ? data.series : []
  const tokens = series.map((x: any) => (typeof x?.totalTokens === "number" ? x.totalTokens : 0))
  const todayTokens = tokens.length > 0 ? tokens[tokens.length - 1] : undefined
  const info: ActivityInfo = {
    totalTokens: typeof s.totalTokens === "number" ? s.totalTokens : undefined,
    durationLabel: typeof s.totalUsageDurationMs === "number" ? fmtDuration(s.totalUsageDurationMs) : undefined,
    currentStreakDays: typeof s.currentStreakDays === "number" ? s.currentStreakDays : undefined,
    longestStreakDays: typeof s.longestStreakDays === "number" ? s.longestStreakDays : undefined,
    todayTokens,
    peakTokens: typeof s.peakDailyTokens === "number" ? s.peakDailyTokens : undefined,
    peakDate: shortDate(s.peakDailyTokensDate),
    spark: sparkline(tokens),
    fetchedAt: now,
  }
  const hasAny =
    info.totalTokens != null ||
    info.todayTokens != null ||
    info.currentStreakDays != null ||
    info.durationLabel != null
  return hasAny ? info : undefined
}

// ---------- 格式化 ----------

function fmtTokens(n: number): string {
  if (n >= 1e9) return `${(n / 1e9).toFixed(2)}G`
  if (n >= 1e6) return `${Math.round(n / 1e6)}M`
  if (n >= 1e3) return `${Math.round(n / 1e3)}k`
  return String(n)
}

/** 终端显示宽度：CJK/全角按 2 列、其余按 1 列 */
function displayWidth(s: string): number {
  let w = 0
  for (const ch of s) {
    const code = ch.codePointAt(0) ?? 0
    const wide =
      (code >= 0x1100 && code <= 0x115f) ||
      code === 0x2329 ||
      code === 0x232a ||
      (code >= 0x2e80 && code <= 0xa4cf && code !== 0x303f) ||
      (code >= 0xac00 && code <= 0xd7a3) ||
      (code >= 0xf900 && code <= 0xfaff) ||
      (code >= 0xfe10 && code <= 0xfe19) ||
      (code >= 0xfe30 && code <= 0xfe6f) ||
      (code >= 0xff00 && code <= 0xff60) ||
      (code >= 0xffe0 && code <= 0xffe6) ||
      (code >= 0x20000 && code <= 0x3fffd)
    w += wide ? 2 : 1
  }
  return w
}

/** 用空格补齐到目标显示宽度（超宽则原样返回） */
function padLabel(s: string, width: number): string {
  const w = displayWidth(s)
  return w >= width ? s : s + " ".repeat(width - w)
}

/** 返回不带 "?" 的查询串：startTime=..&endTime=..（近 24 小时） */
function usageWindow(): string {
  const now = new Date()
  const start = new Date(now.getTime() - 24 * 3600 * 1000)
  const pad = (x: number) => String(x).padStart(2, "0")
  const fmt = (d: Date) =>
    `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
  return `startTime=${encodeURIComponent(fmt(start))}&endTime=${encodeURIComponent(fmt(now))}`
}

/** 返回不带 "?" 的查询串：近一年（活跃度） */
function activityWindow(): string {
  const now = new Date()
  const start = new Date(now.getTime() - 365 * 24 * 3600 * 1000)
  const pad = (x: number) => String(x).padStart(2, "0")
  const fmtDate = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
  return `startTime=${encodeURIComponent(`${fmtDate(start)} 00:00:00`)}&endTime=${encodeURIComponent(`${fmtDate(now)} 23:59:59`)}`
}

// ---------- UI 组件 ----------

function BarRow(props: { label: string; limit?: LimitInfo; theme: () => any; colorFor: (r?: number) => any }) {
  const width = 10
  const lim = () => props.limit
  const filled = () => {
    const r = lim()?.remaining
    return r == null ? 0 : Math.round((Math.max(0, Math.min(100, r)) / 100) * width)
  }
  return (
    <box flexDirection="column" gap={0}>
      <box flexDirection="row" gap={1}>
        <text fg={props.theme()?.textMuted}>{padLabel(props.label, 8)}</text>
        <text>
          <span style={{ fg: props.colorFor(lim()?.remaining) }}>{"█".repeat(filled())}</span>
          <span style={{ fg: props.theme()?.textMuted }}>{"░".repeat(width - filled())}</span>
        </text>
        <text fg={props.colorFor(lim()?.remaining)}>{lim()?.remaining != null ? `${lim()?.remaining}%` : "?"}</text>
        <Show when={lim()?.resetLabel}>
          <text fg={props.theme()?.textMuted}>{`↻ ${lim()?.resetLabel}`}</text>
        </Show>
      </box>
      <Show when={lim()?.detail}>
        <box flexDirection="row" gap={1} paddingLeft={11}>
          <text fg={props.theme()?.textMuted}>{`${lim()?.detailLabel ?? "已用"} ${lim()?.detail}`}</text>
        </box>
      </Show>
    </box>
  )
}

function Row(props: { label: string; value: string; theme: () => any }) {
  return (
    <box flexDirection="row" gap={1}>
      <text fg={props.theme()?.textMuted}>{props.label}</text>
      <text fg={props.theme()?.text}>{props.value}</text>
    </box>
  )
}

function Indent(props: { name: string; value: string; theme: () => any }) {
  return (
    <box flexDirection="row" gap={1} paddingLeft={2}>
      <text fg={props.theme()?.textMuted}>{`· ${props.name}`}</text>
      <text fg={props.theme()?.text}>{props.value}</text>
    </box>
  )
}

function GlmSidebar(props: { api: TuiApi; usage: () => UsageInfo | null }): JSX.Element {
  const theme = () => props.api?.theme?.current
  const u = () => props.usage()
  const colorFor = (r: number | undefined) => {
    const t = theme()
    if (!t) return undefined
    if (r == null) return t.warning
    if (r > 40) return t.success
    if (r > 15) return t.warning
    return t.error
  }
  const visible = () => !!findGlmProvider(props.api) || !!u()
  const hasData = () =>
    !!(
      u()?.fiveHour ||
      u()?.week ||
      u()?.mcp ||
      u()?.tokens24h != null ||
      u()?.models?.length ||
      u()?.tools?.length ||
      u()?.activity
    )
  const activity = () => u()?.activity
  const cumulative = () => {
    const a = activity()
    if (a?.totalTokens == null) return undefined
    return a.durationLabel ? `${fmtTokens(a.totalTokens)} tokens · ${a.durationLabel}` : `${fmtTokens(a.totalTokens)} tokens`
  }
  const todayLine = () => {
    const a = activity()
    if (a?.todayTokens == null && a?.peakTokens == null) return undefined
    const today = a?.todayTokens != null ? fmtTokens(a.todayTokens) : "?"
    const peak =
      a?.peakTokens != null
        ? ` · 峰值 ${fmtTokens(a.peakTokens)}${a?.peakDate ? ` @ ${a.peakDate}` : ""}`
        : ""
    return `${today}${peak}`
  }
  const streakLine = () => {
    const a = activity()
    if (a?.currentStreakDays == null && a?.longestStreakDays == null) return undefined
    const cur = a?.currentStreakDays != null ? `${a.currentStreakDays} 天` : "?"
    const longest =
      a?.longestStreakDays != null && a.longestStreakDays !== a?.currentStreakDays
        ? ` · 最长 ${a.longestStreakDays}`
        : ""
    return `${cur}${longest}`
  }

  return (
    <Show when={visible()}>
      <box flexDirection="column" gap={0} paddingTop={1}>
        <box flexDirection="row" gap={1}>
          <text fg={theme()?.accent}>
            <b>GLM 用量</b>
          </text>
          <Show when={u()?.level}>
            <text fg={theme()?.textMuted}>{`[${u()?.level}]`}</text>
          </Show>
        </box>
        <Show
          when={!u()?.error}
          fallback={<text fg={theme()?.warning}>{u()?.error ?? "?"}</text>}
        >
          <Show when={u()} fallback={<text fg={theme()?.textMuted}>加载中…</text>}>
            <Show when={hasData()} fallback={<text fg={theme()?.textMuted}>暂无配额数据</text>}>
              <Show when={u()?.fiveHour}>
                <BarRow label={u()?.fiveHour?.label ?? "5h"} limit={u()?.fiveHour} theme={theme} colorFor={colorFor} />
              </Show>
              <Show when={u()?.week}>
                <BarRow label={u()?.week?.label ?? "周额度"} limit={u()?.week} theme={theme} colorFor={colorFor} />
              </Show>
              <Show when={u()?.tokens24h != null}>
                <Row label="24h" value={`${fmtTokens(u()?.tokens24h as number)} tokens`} theme={theme} />
              </Show>
              <Show when={u()?.models?.length}>
                <For each={u()?.models}>
                  {(m: ModelUsage) => <Indent name={m.name} value={fmtTokens(m.tokens)} theme={theme} />}
                </For>
              </Show>
              <Show when={u()?.mcp}>
                <BarRow label="MCP 月度" limit={u()?.mcp} theme={theme} colorFor={colorFor} />
              </Show>
              <Show when={u()?.tools?.length}>
                <For each={u()?.tools}>
                  {(t: ToolUsage) => <Indent name={t.name} value={String(t.count)} theme={theme} />}
                </For>
              </Show>
              <Show when={activity()}>
                <box flexDirection="row" gap={1} paddingTop={1}>
                  <text fg={theme()?.textMuted}>活跃度</text>
                </box>
                <Show when={cumulative()}>
                  <Indent name="累计" value={cumulative() as string} theme={theme} />
                </Show>
                <Show when={todayLine()}>
                  <Indent name="今日" value={todayLine() as string} theme={theme} />
                </Show>
                <Show when={streakLine()}>
                  <Indent name="连续" value={streakLine() as string} theme={theme} />
                </Show>
                <Show when={activity()?.spark}>
                  <Indent name="近14天" value={activity()?.spark as string} theme={theme} />
                </Show>
              </Show>
            </Show>
          </Show>
        </Show>
      </box>
    </Show>
  )
}

// ---------- 插件主体 ----------

/** 活跃度数据刷新间隔（年窗口响应较大，独立降频） */
const ACTIVITY_TTL = 300_000

const tui: TuiPlugin = async (rawApi, rawOptions) => {
  const api = rawApi as unknown as TuiApi
  const opts = (rawOptions ?? {}) as PluginOptions
  const [usage, setUsage] = createSignal<UsageInfo | null>(null)
  let lastFetch = 0
  let lastActivityFetch = 0
  let activity: ActivityInfo | undefined
  let timer: ReturnType<typeof setInterval> | undefined
  let loggedRaw = false

  function log(level: "info" | "warn" | "error", message: string, extra?: Record<string, unknown>) {
    const fn = level === "error" ? "error" : level === "warn" ? "warn" : "log"
    try {
      console[fn](`[glm-balance] ${message}`, extra ?? "")
    } catch {
      /* ignore */
    }
    try {
      api?.client?.app?.log?.({ body: { service: "glm-balance", level, message, extra } })
    } catch {
      /* ignore */
    }
  }

  async function refresh(force = false) {
    const now = Date.now()
    try {
      if (!force && now - lastFetch < 30000) return
      const p = findGlmProvider(api)
      const info = inspectProvider(p)
      const token = providerToken(p)
      const baseURL = info.baseURL
      if (!token || !baseURL) {
        setUsage({ error: "未找到 GLM provider", fetchedAt: now })
        return
      }
      lastFetch = now
      const base = opts.monitorBase?.trim() || monitorBaseFrom(baseURL)
      const org = opts.organization?.trim()
      const project = opts.project?.trim()
      const usageType = typeof opts.usageType === "number" ? opts.usageType : org ? 2 : undefined

      const headers: Record<string, string> = {
        Authorization: token,
        "Accept-Language": "en-US,en",
        "Content-Type": "application/json",
      }
      if (org) headers["bigmodel-organization"] = org
      if (project) headers["bigmodel-project"] = project

      const typeQ = usageType != null ? `type=${usageType}&` : ""
      const quotaUrl = `${base}/api/monitor/usage/quota/limit?${typeQ}`
      const modelUrl = `${base}/api/monitor/usage/model-usage?${typeQ}${usageWindow()}`
      const toolUrl = `${base}/api/monitor/usage/tool-usage?${typeQ}${usageWindow()}`

      // 活跃度：独立节流（默认开启，activity:false 完全跳过）
      const wantActivity = opts.activity !== false
      if (wantActivity && now - lastActivityFetch >= ACTIVITY_TTL) {
        lastActivityFetch = now
        try {
          const aType = typeof opts.activityType === "number" ? opts.activityType : 3
          const aBase = opts.activityBase?.trim() || base
          const res4 = await fetch(`${aBase}/api/monitor/credit-usage/activity?type=${aType}&${activityWindow()}`, {
            method: "GET",
            headers,
          })
          const t4 = await res4.text()
          if (res4.ok && t4.trim()) {
            const j4 = JSON.parse(t4)
            if (j4 && j4.success !== false) {
              const parsed = parseActivity(j4, now)
              if (parsed) activity = parsed
            } else {
              log("warn", "activity 业务失败", { code: j4?.code, msg: j4?.msg })
            }
          } else {
            log("warn", `activity HTTP ${res4.status}`)
          }
        } catch (e: any) {
          lastActivityFetch = 0
          log("warn", "activity 查询失败", { error: e?.message ?? String(e) })
        }
      }

      try {
        const [res, modelRes, toolRes] = await Promise.all([
          fetch(quotaUrl, { method: "GET", headers }),
          fetch(modelUrl, { method: "GET", headers }).catch(() => null),
          fetch(toolUrl, { method: "GET", headers }).catch(() => null),
        ])
        const text = await res.text()
        if (!res.ok) {
          setUsage({ error: `HTTP ${res.status}`, fetchedAt: now })
          log("warn", `quota/limit HTTP ${res.status}`, { url: quotaUrl, body: text.slice(0, 500) })
          return
        }
        let json: any
        try {
          json = JSON.parse(text)
        } catch {
          json = { raw: text }
        }
        if (!loggedRaw) {
          loggedRaw = true
          log("info", "quota/limit 原始响应(仅首次)", { url: quotaUrl, json })
        }
        if (json && json.success === false) {
          const msg = typeof json.msg === "string" && json.msg ? json.msg : `code ${json.code ?? "?"}`
          setUsage({ error: msg, fetchedAt: now })
          log("warn", "quota/limit 业务失败", { url: quotaUrl, code: json.code, msg })
          return
        }
        const data = json?.data ?? json
        const { fiveHour, week, mcp } = parseQuotaLimits(json, now)
        const level = typeof data?.level === "string" ? data.level : undefined

        let tokens24h: number | undefined
        let models: ModelUsage[] | undefined
        if (modelRes && modelRes.ok) {
          try {
            const text2 = await modelRes.text()
            if (text2.trim()) {
              const mj = JSON.parse(text2)
              if (mj && mj.success !== false) {
                const t = mj?.data?.totalUsage?.totalTokensUsage
                if (typeof t === "number") tokens24h = t
                const ms = mj?.data?.totalUsage?.modelSummaryList
                if (Array.isArray(ms)) {
                  models = ms
                    .map((m: any) => ({ name: String(m?.modelName ?? "?"), tokens: Number(m?.totalTokens ?? 0) }))
                    .filter((m: ModelUsage) => m.tokens > 0)
                    .sort((a: ModelUsage, b: ModelUsage) => b.tokens - a.tokens)
                }
              }
            }
          } catch {
            /* ignore */
          }
        }

        let tools: ToolUsage[] | undefined
        if (toolRes && toolRes.ok) {
          try {
            const text3 = await toolRes.text()
            if (text3.trim()) {
              const tj = JSON.parse(text3)
              if (tj && tj.success !== false) {
                const ts = tj?.data?.totalUsage?.toolSummaryList
                if (Array.isArray(ts)) {
                  tools = ts
                    .map((t: any) => ({
                      name: String(t?.toolNameI18n || t?.toolName || t?.toolCode || "?"),
                      count: Number(t?.totalUsageCount ?? 0),
                    }))
                    .filter((t: ToolUsage) => t.count > 0)
                    .sort((a: ToolUsage, b: ToolUsage) => b.count - a.count)
                }
              }
            }
          } catch {
            /* ignore */
          }
        }

        setUsage({ fiveHour, week, mcp, level, tokens24h, models, tools, activity, fetchedAt: now })
      } catch (e: any) {
        setUsage({ error: e?.message ?? String(e), fetchedAt: now })
        log("error", "quota/limit 查询失败", { url: quotaUrl, error: e?.message ?? String(e) })
      }
    } catch (e: any) {
      setUsage({ error: e?.message ?? String(e), fetchedAt: now })
      log("error", "refresh 异常", { error: e?.message ?? String(e) })
    }
  }

  void refresh(true)
  timer = setInterval(() => void refresh(), 30000)

  let offIdle: (() => void) | undefined
  try {
    offIdle = api?.event?.on?.("session.idle", () => void refresh())
  } catch {
    /* ignore */
  }

  try {
    api?.lifecycle?.onDispose?.(() => {
      if (timer) clearInterval(timer)
      try {
        offIdle?.()
      } catch {
        /* ignore */
      }
    })
  } catch {
    /* ignore */
  }

  try {
    api?.slots?.register?.({
      order: 50,
      slots: {
        sidebar_content() {
          return <GlmSidebar api={api} usage={usage} />
        },
      },
    })
  } catch (e: any) {
    log("error", "slots.register 注册失败", { error: e?.message ?? String(e) })
  }
}

export default { id: "glm-balance", tui }
