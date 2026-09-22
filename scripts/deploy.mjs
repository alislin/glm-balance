import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs"
import { dirname, resolve } from "node:path"
import { homedir } from "node:os"
import process from "node:process"

const args = process.argv.slice(2)
let configDir = resolve(homedir(), ".config", "opencode")
for (let i = 0; i < args.length; i++) {
  if (args[i] === "--config-dir" && args[i + 1]) {
    configDir = resolve(args[++i])
  }
}

const root = resolve(import.meta.dirname, "..")
const dist = resolve(root, "dist")
const targetDir = resolve(configDir, "plugins", "glm-balance")
const target = resolve(targetDir, "index.js")
const tuiPath = resolve(configDir, "tui.json")
const targetRef = target.replaceAll("\\", "/")

const matchesPlugin = (path) => {
  const p = String(path).replaceAll("\\", "/").toLowerCase()
  return p.includes("glm-balance") && (p.endsWith("/index.tsx") || p.endsWith("/index.js"))
}

if (!existsSync(dist)) {
  console.error(`未找到构建产物 ${dist}，请先运行 npm run build`)
  process.exit(1)
}

let config
if (existsSync(tuiPath)) {
  try {
    config = JSON.parse(readFileSync(tuiPath, "utf8"))
  } catch (err) {
    console.error(`解析 ${tuiPath} 失败：${err.message}`)
    process.exit(1)
  }
} else {
  config = {}
}
if (typeof config !== "object" || config === null || Array.isArray(config)) {
  console.error(`${tuiPath} 不是有效的 opencode 配置对象`)
  process.exit(1)
}
if (!config.$schema) config.$schema = "https://opencode.ai/tui.json"
if (!Array.isArray(config.plugin)) config.plugin = []

let updated = 0
config.plugin = config.plugin.map((entry) => {
  const path = Array.isArray(entry) ? entry[0] : entry
  if (matchesPlugin(path)) {
    updated++
    return Array.isArray(entry) ? [targetRef, entry[1]] : targetRef
  }
  return entry
})
if (updated === 0) {
  config.plugin.push(targetRef)
}

rmSync(targetDir, { recursive: true, force: true })
cpSync(dist, targetDir, { recursive: true })
mkdirSync(dirname(tuiPath), { recursive: true })
writeFileSync(tuiPath, JSON.stringify(config, null, 2) + "\n")

console.log(`产物已部署：${targetDir}`)
console.log(
  updated === 0
    ? `tui.json 已新增插件条目：${tuiPath}`
    : `tui.json 已更新 ${updated} 个插件条目（选项保留）：${tuiPath}`,
)
console.log("重启 opencode 后生效")
