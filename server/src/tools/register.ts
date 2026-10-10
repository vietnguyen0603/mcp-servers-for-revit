import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";
import {
  ToolCatalog,
  readCatalogOptions,
  type CatalogOptions,
} from "../catalog/ToolCatalog.js";
import { registerCatalogTools } from "../catalog/metaTools.js";

// The HTTP server registers the tools once per session; log the list only once
let loggedRegistration = false;

export async function registerTools(
  server: McpServer,
  options: CatalogOptions = readCatalogOptions()
): Promise<ToolCatalog> {
  const catalog = new ToolCatalog(server);
  const capturingServer = catalog.capturingServer();

  // The single-exe build (scripts/build-exe.ts) has no tools directory to scan,
  // so its entry point hands over the statically imported tool modules instead
  const bundled = (globalThis as { __REVIT_TOOL_MODULES__?: Record<string, Record<string, unknown>> })
    .__REVIT_TOOL_MODULES__;

  // Keep .ts or .js files, excluding the index and register files
  const toolFiles = (bundled ? Object.keys(bundled) : fs.readdirSync(path.dirname(fileURLToPath(import.meta.url)))).filter(
    (file) =>
      (file.endsWith(".ts") || file.endsWith(".js")) &&
      file !== "index.ts" &&
      file !== "index.js" &&
      file !== "register.ts" &&
      file !== "register.js"
  );

  // Dynamically import and register each tool
  for (const file of toolFiles) {
    try {
      // Dynamically import the module
      const module = bundled?.[file] ?? (await import(`./${file.replace(/\.(ts|js)$/, ".js")}`));

      // Find and call the register function
      const registerFunctionName = Object.keys(module).find(
        (key) => key.startsWith("register") && typeof module[key] === "function"
      );

      if (registerFunctionName) {
        module[registerFunctionName](capturingServer);
        if (!loggedRegistration) console.error(`Registered tool: ${file}`);
      } else {
        console.warn(`Warning: no register function found in file ${file}`);
      }
    } catch (error) {
      console.error(`Error registering tool ${file}:`, error);
    }
  }

  // Decide which tools to expose per catalog, and register the catalog meta-tools in dynamic mode
  catalog.finalize(options);
  if (options.mode === "dynamic") {
    registerCatalogTools(server, catalog);
  }
  if (!loggedRegistration) console.error(`Tool mode: ${options.mode}`);
  loggedRegistration = true;

  return catalog;
}
