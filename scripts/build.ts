import { rmSync } from "node:fs"
import { readFileSync } from "node:fs"
import { resolve } from "node:path"

rmSync(resolve(import.meta.dir, "../dist"), { recursive: true, force: true })

const result = await Bun.build({
  entrypoints: ["src/index.tsx"],
  outdir: "dist",
  target: "bun",
  plugins: [
    {
      name: "stub-opentui-platforms",
      setup(build) {
        build.onResolve({ filter: /^@opentui\/core-(darwin|linux|win32-arm64)/ }, (args) => ({
          path: args.path,
          namespace: "stub-opentui",
        }))
        build.onLoad({ filter: /.*/, namespace: "stub-opentui" }, () => ({
          contents: `export default ""`,
          loader: "js",
        }))
        build.onResolve({ filter: /^@opentui\/core\/parser\.worker$/ }, (args) => ({
          path: args.path,
          namespace: "opentui-file",
        }))
        build.onLoad({ filter: /.*/, namespace: "opentui-file" }, () => ({
          contents: readFileSync(resolve(import.meta.dir, "../node_modules/@opentui/core/parser.worker.js")),
          loader: "file",
        }))
      },
    },
  ],
})

if (!result.success) {
  for (const log of result.logs) console.error(log)
  process.exit(1)
}
console.log("构建完成：dist/")
