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

export async function registerTools(
  server: McpServer,
  options: CatalogOptions = readCatalogOptions()
): Promise<ToolCatalog> {
  const catalog = new ToolCatalog(server);
  const capturingServer = catalog.capturingServer();

  // Get the directory of the current file
  const __filename = fileURLToPath(import.meta.url);
  const __dirname = path.dirname(__filename);

  // Read all files in the tools directory
  const files = fs.readdirSync(__dirname);

  // Keep .ts or .js files, excluding the index and register files
  const toolFiles = files.filter(
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
      // Build the import path
      const importPath = `./${file.replace(/\.(ts|js)$/, ".js")}`;

      // Dynamically import the module
      const module = await import(importPath);

      // Find and call the register function
      const registerFunctionName = Object.keys(module).find(
        (key) => key.startsWith("register") && typeof module[key] === "function"
      );

      if (registerFunctionName) {
        module[registerFunctionName](capturingServer);
        console.error(`Registered tool: ${file}`);
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
  console.error(`Tool mode: ${options.mode}`);

  return catalog;
}
