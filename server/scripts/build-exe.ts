// Builds a self-contained Windows executable of the MCP server (no Node, no
// node_modules). Run from server/: `bun scripts/build-exe.ts`
// Output: dist/revit-mcp.exe - point an MCP client at it as a stdio command.
import fs from "fs";
import path from "path";

const root = path.resolve(import.meta.dir, "..");
const toolsDir = path.join(root, "src", "tools");
const workDir = path.join(root, ".exe-build");

// register.ts scans its directory at runtime; a compiled exe has none, so
// import every tool statically and hand the modules over through a global
const tools = fs
  .readdirSync(toolsDir)
  .filter((file) => file.endsWith(".ts") && file !== "index.ts" && file !== "register.ts");

fs.mkdirSync(workDir, { recursive: true });
// db.ts defaults next to the source tree, which an exe does not have. Its own
// module, imported first, because the tool imports below load db.ts eagerly.
fs.writeFileSync(
  path.join(workDir, "env.ts"),
  [
    `import { join } from "path";`,
    `import { mkdirSync } from "fs";`,
    `if (!process.env.REVIT_MCP_DB_PATH?.trim()) {`,
    `  const dir = join(process.env.LOCALAPPDATA ?? process.env.HOME ?? ".", "revit-mcp");`,
    `  mkdirSync(dir, { recursive: true });`,
    `  process.env.REVIT_MCP_DB_PATH = join(dir, "revit-data.db");`,
    `}`,
    ``,
  ].join("\n"),
);
const entry = path.join(workDir, "entry.ts");
fs.writeFileSync(
  entry,
  [
    `import "./env.ts";`,
    ...tools.map((file, i) => `import * as t${i} from "../src/tools/${file}";`),
    `(globalThis as Record<string, unknown>).__REVIT_TOOL_MODULES__ = {`,
    ...tools.map((file, i) => `  ${JSON.stringify(file)}: t${i},`),
    `};`,
    // dynamic so the global is set before index.ts starts registering tools
    `await import("../src/index.ts");`,
    ``,
  ].join("\n"),
);

const shim = path.join(import.meta.dir, "bun-sqlite-shim.ts");
const result = await Bun.build({
  entrypoints: [entry],
  compile: { target: "bun-windows-x64", outfile: path.join(root, "dist", "revit-mcp.exe") },
  minify: true,
  plugins: [
    {
      name: "better-sqlite3-to-bun-sqlite",
      setup(build) {
        build.onResolve({ filter: /^better-sqlite3$/ }, () => ({ path: shim }));
      },
    },
  ],
});

fs.rmSync(workDir, { recursive: true, force: true });
if (!result.success) {
  result.logs.forEach((log) => console.error(log));
  process.exit(1);
}
console.log(`Built ${result.outputs.map((o) => o.path).join(", ")} with ${tools.length} tools`);
