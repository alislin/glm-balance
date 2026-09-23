import { rmSync } from "node:fs"
import { resolve } from "node:path"

rmSync(resolve(import.meta.dir, "../dist"), { recursive: true, force: true })

const result = await Bun.build({
  entrypoints: ["src/index.tsx"],
  outdir: "dist",
  target: "bun",
  external: [
    "solid-js",
    "solid-js/store",
    "@opentui/solid",
    "@opentui/solid/components",
    "@opentui/solid/jsx-runtime",
    "@opentui/solid/jsx-dev-runtime",
    "@opentui/core",
    "@opentui/core/testing",
    "@opentui/keymap",
    "@opentui/keymap/solid",
    "@opentui/keymap/extras",
    "@opentui/keymap/runtime-modules",
    "@opencode-ai/plugin",
    "@opencode-ai/plugin/tui",
  ],
})

if (!result.success) {
  for (const log of result.logs) console.error(log)
  process.exit(1)
}
console.log("构建完成：dist/")
