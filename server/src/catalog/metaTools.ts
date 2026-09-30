import { z } from "zod";
import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { CATALOGS, CORE_CATALOG, resolveCatalog } from "./catalogs.js";
import { errorResult, type ToolCatalog } from "./ToolCatalog.js";

const CATALOG_NAMES = CATALOGS.filter((catalog) => catalog.id !== CORE_CATALOG)
  .map((catalog) => catalog.id)
  .join(", ");

function jsonResult(value: unknown) {
  return { content: [{ type: "text" as const, text: JSON.stringify(value, null, 2) }] };
}

function unknownCatalogMessage(name: string): string {
  return `Unknown catalog "${name}". Valid catalogs: ${CATALOG_NAMES}.`;
}

/**
 * Register the layer-1 meta tools that let a model discover and load layer-2
 * tools: `search_tools`, `enable_catalog` and `call_tool`.
 */
export function registerCatalogTools(server: McpServer, catalog: ToolCatalog): void {
  const catalogLines = catalog
    .catalogs()
    .filter((summary) => summary.id !== CORE_CATALOG && summary.tools.length > 0)
    .map((summary) => `- ${summary.id}: ${summary.description} (${summary.tools.length} tools)`)
    .join("\n");

  server.tool(
    "search_tools",
    "Search the Revit tool catalog for tools that are not loaded yet. Returns matching tool names, catalogs, " +
      "whether each is read-only and whether it is already enabled. Pass a catalog with no query to list that catalog. " +
      "Set includeSchema to get input schemas for use with call_tool.\nCatalogs:\n" +
      catalogLines,
    {
      query: z
        .string()
        .max(200)
        .optional()
        .describe("Keywords describing the task, e.g. 'beam register span' or 'tag rooms'."),
      catalog: z
        .string()
        .max(64)
        .optional()
        .describe(`Restrict results to one catalog (id or alias): ${CATALOG_NAMES}.`),
      includeSchema: z
        .boolean()
        .optional()
        .default(false)
        .describe("Include each tool's JSON input schema."),
      limit: z.number().int().min(1).max(50).optional().default(10).describe("Maximum number of results."),
    },
    async (args) => {
      let catalogId: string | undefined;
      if (args.catalog) {
        catalogId = resolveCatalog(args.catalog)?.id;
        if (!catalogId) return errorResult(unknownCatalogMessage(args.catalog));
      }

      const matches = catalog.search({
        query: args.query,
        catalogId,
        limit: args.limit,
        includeSchema: args.includeSchema,
      });

      return jsonResult({
        matches,
        hint:
          matches.length === 0
            ? `No tools matched. Try other keywords or browse a catalog: ${CATALOG_NAMES}.`
            : "Enable a catalog with enable_catalog to call its tools directly. Read-only tools can also be run with call_tool.",
      });
    }
  );

  server.tool(
    "enable_catalog",
    "Load every tool in one or more catalogs so they can be called directly. Accepts ids or aliases such as " +
      "'arch', 'struct' or 'drafting'. Tools that modify the model (create, tag, modify, delete, run code) " +
      `must be loaded this way before use. Catalogs: ${CATALOG_NAMES}.`,
    {
      catalogs: z
        .array(z.string().min(1).max(64))
        .min(1)
        .max(CATALOGS.length)
        .describe("Catalog ids or aliases to enable."),
    },
    async (args) => {
      const ids: string[] = [];
      for (const name of args.catalogs) {
        const resolved = resolveCatalog(name);
        if (!resolved) return errorResult(unknownCatalogMessage(name));
        if (!ids.includes(resolved.id)) ids.push(resolved.id);
      }

      const { enabled, alreadyEnabled } = catalog.enableCatalogs(ids);
      return jsonResult({
        catalogs: ids,
        enabled,
        alreadyEnabled,
        note:
          "The listed tools are now available as native tools. If they do not appear in your tool list, " +
          "this client does not refresh tools mid-session: use call_tool for read-only tools, or ask the user to set REVIT_MCP_TOOL_MODE=all.",
      });
    }
  );

  server.tool(
    "call_tool",
    "Run a read-only catalog tool by name without loading its catalog, e.g. get_beam_register_data. " +
      "Use search_tools with includeSchema first to get its arguments. Tools that modify the model are refused; " +
      "load them with enable_catalog instead.",
    {
      name: z.string().min(1).max(128).describe("Exact tool name returned by search_tools."),
      arguments: z
        .record(z.unknown())
        .optional()
        .default({})
        .describe("Arguments object matching the tool's input schema."),
    },
    async (args, extra) => catalog.call(args.name, args.arguments, extra)
  );
}
