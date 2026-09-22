import { readFileSync } from "node:fs"
import { join } from "node:path"
import plugin from "../src/index.tsx"

const home = process.env.USERPROFILE ?? process.env.HOME ?? ""
const auth = JSON.parse(readFileSync(join(home, ".local", "share", "opencode", "auth.json"), "utf8"))
const key = auth["zhipuai-coding-plan"].key

const logs: any[] = []
let registered: any = null
const api = {
  client: { app: { log: async (a: any) => { logs.push(a.body) } } },
  event: { on: () => () => {} },
  lifecycle: { onDispose: () => {} },
  slots: { register: (o: any) => { registered = o } },
  theme: { current: undefined },
  state: {
    provider: [
      { id: "zhipuai-coding-plan", options: { baseURL: "https://open.bigmodel.cn/api/anthropic", apiKey: key } },
    ],
  },
}

await (plugin as any).tui(api, {
  organization: "org-cAa17C5555f640bcA1A7C3328c8F10e5",
  project: "proj_AC47bb1eF49843248590583BE76D6679",
  activity: true,
})
await new Promise((r) => setTimeout(r, 6000))
console.log("slots.registered:", !!registered)
const first = logs.find((l) => l.message.includes("原始响应"))
if (first) {
  const limits = first.extra?.json?.data?.limits ?? []
  console.log("quota limits:", limits.map((l: any) => `${l.type}/unit=${l.unit}/${l.percentage}%`).join(", "))
  console.log("level:", first.extra?.json?.data?.level)
} else {
  console.log("quota 原始响应日志缺失！logs:", logs.map((l) => l.message).join(" | "))
}
process.exit(0)
