import type { McpServer, RegisteredTool } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import {
  getParseErrorMessage,
  normalizeObjectSchema,
  safeParseAsync,
} from "@modelcontextprotocol/sdk/server/zod-compat.js";
import { toJsonSchemaCompat } from "@modelcontextprotocol/sdk/server/zod-json-schema-compat.js";
import { CATALOGS, CORE_CATALOG, resolveCatalog } from "./catalogs.js";
import { TOOL_MANIFEST, type ToolManifestEntry } from "./toolManifest.js";

/**
 * - `dynamic`: expose core tools plus the catalog meta tools; other catalogs
 *   are enabled on demand. Requires client support for tools/list_changed to
 *   use tools that modify the model.
 * - `all`: expose every tool up front (previous behaviour).
 */
export type ToolMode = "dynamic" | "all";

export interface CatalogOptions {
  mode: ToolMode;
  /** Catalog ids enabled at startup in addition to core. */
  preload: string[];
}

/** Read `REVIT_MCP_TOOL_MODE` and `REVIT_MCP_CATALOGS` from the environment. */
export function readCatalogOptions(env: NodeJS.ProcessEnv = process.env): CatalogOptions {
  const rawMode = (env.REVIT_MCP_TOOL_MODE ?? "dynamic").trim().toLowerCase();
  let mode: ToolMode = "dynamic";
  if (rawMode === "all") {
    mode = "all";
  } else if (rawMode !== "dynamic") {
    console.error(`Unknown REVIT_MCP_TOOL_MODE "${rawMode}", using "dynamic"`);
  }

  const preload: string[] = [];
  for (const name of (env.REVIT_MCP_CATALOGS ?? "").split(",")) {
    if (!name.trim()) continue;
    const catalog = resolveCatalog(name);
    if (catalog) {
      preload.push(catalog.id);
    } else {
      console.error(`Unknown catalog "${name.trim()}" in REVIT_MCP_CATALOGS, ignoring`);
    }
  }

  return { mode, preload };
}

interface CatalogEntry {
  name: string;
  handle: RegisteredTool;
  /** Undefined for tools missing from TOOL_MANIFEST; those stay always enabled. */
  meta: ToolManifestEntry | undefined;
}

export interface ToolSearchHit {
  name: string;
  catalogs: string[];
  readOnly: boolean;
  enabled: boolean;
  summary: string;
  inputSchema?: Record<string, unknown>;
}

export interface CatalogSummary {
  id: string;
  title: string;
  description: string;
  tools: string[];
}

type ToolHandler = (args: unknown, extra: unknown) => CallToolResult | Promise<CallToolResult>;

const SUMMARY_MAX_LENGTH = 200;

export function errorResult(text: string): CallToolResult {
  return { content: [{ type: "text", text }], isError: true };
}

function summarize(description: string | undefined): string {
  const text = (description ?? "").replace(/\s+/g, " ").trim();
  const sentence = text.match(/^.*?\.(\s|$)/)?.[0].trim() ?? text;
  return sentence.length > SUMMARY_MAX_LENGTH
    ? `${sentence.slice(0, SUMMARY_MAX_LENGTH - 1)}…`
    : sentence;
}

function tokenize(query: string): string[] {
  return query
    .toLowerCase()
    .split(/[^a-z0-9#]+/)
    .filter((token) => token.length > 1);
}

/**
 * Two-layer tool registry. Layer 1 (`core`) is always exposed; layer 2 tools
 * are registered with the SDK but disabled until their catalog is enabled,
 * which keeps them out of `tools/list` and out of the model's context.
 */
export class ToolCatalog {
  private readonly entries = new Map<string, CatalogEntry>();

  constructor(private readonly server: McpServer) {}

  /**
   * A view of the server that records every tool registered through it, so
   * tool files keep calling `server.tool(...)` unchanged.
   */
  capturingServer(): McpServer {
    const entries = this.entries;
    return new Proxy(this.server, {
      get(target, prop) {
        const value = Reflect.get(target, prop, target);
        if (typeof value !== "function") return value;
        if (prop === "tool" || prop === "registerTool") {
          return (name: string, ...rest: unknown[]) => {
            const handle = (value as (...args: unknown[]) => RegisteredTool).call(target, name, ...rest);
            entries.set(name, { name, handle, meta: TOOL_MANIFEST[name] });
            return handle;
          };
        }
        return value.bind(target);
      },
    });
  }

  /** Apply MCP annotations and the startup exposure for the given options. */
  finalize(options: CatalogOptions): void {
    for (const entry of this.entries.values()) {
      if (!entry.meta) {
        console.error(`Tool "${entry.name}" has no entry in TOOL_MANIFEST; exposing it in every mode`);
        continue;
      }
      entry.handle.annotations = {
        ...entry.handle.annotations,
        readOnlyHint: entry.meta.readOnly,
        ...(entry.meta.readOnly ? {} : { destructiveHint: entry.meta.destructive ?? false }),
      };
      if (options.mode === "dynamic") {
        entry.handle.enabled = entry.meta.catalogs.includes(CORE_CATALOG);
      }
    }
    if (options.mode === "dynamic" && options.preload.length > 0) {
      this.enableCatalogs(options.preload);
    }
  }

  catalogs(): CatalogSummary[] {
    return CATALOGS.map((catalog) => ({
      id: catalog.id,
      title: catalog.title,
      description: catalog.description,
      tools: [...this.entries.values()]
        .filter((entry) => entry.meta?.catalogs.includes(catalog.id))
        .map((entry) => entry.name)
        .sort(),
    }));
  }

  /**
   * Enable every tool belonging to the given catalog ids and notify the
   * client once. Callers must pass resolved ids (see `resolveCatalog`).
   */
  enableCatalogs(catalogIds: string[]): { enabled: string[]; alreadyEnabled: string[] } {
    const enabled: string[] = [];
    const alreadyEnabled: string[] = [];
    for (const entry of this.entries.values()) {
      if (!entry.meta?.catalogs.some((id) => catalogIds.includes(id))) continue;
      if (entry.handle.enabled) {
        alreadyEnabled.push(entry.name);
      } else {
        entry.handle.enabled = true;
        enabled.push(entry.name);
      }
    }
    if (enabled.length > 0) {
      this.server.sendToolListChanged();
    }
    return { enabled: enabled.sort(), alreadyEnabled: alreadyEnabled.sort() };
  }

  /**
   * Keyword search over tool names, manifest keywords, catalog names and
   * descriptions. With no query, lists the tools of `catalogId` (or all).
   */
  search(options: { query?: string; catalogId?: string; limit: number; includeSchema: boolean }): ToolSearchHit[] {
    const tokens = tokenize(options.query ?? "");
    const scored: Array<{ entry: CatalogEntry; score: number }> = [];

    for (const entry of this.entries.values()) {
      const catalogIds = entry.meta?.catalogs ?? [];
      if (options.catalogId && !catalogIds.includes(options.catalogId)) continue;

      const name = entry.name.toLowerCase();
      const keywords = (entry.meta?.keywords ?? []).map((keyword) => keyword.toLowerCase());
      const catalogTerms = CATALOGS.filter((catalog) => catalogIds.includes(catalog.id)).flatMap(
        (catalog) => [catalog.id, ...catalog.aliases]
      );
      const description = (entry.handle.description ?? "").toLowerCase();

      let score = 0;
      for (const token of tokens) {
        if (name.includes(token)) score += 3;
        if (keywords.some((keyword) => keyword.includes(token))) score += 2;
        if (catalogTerms.includes(token)) score += 2;
        if (description.includes(token)) score += 1;
      }
      if (tokens.length === 0 || score > 0) {
        scored.push({ entry, score });
      }
    }

    return scored
      .sort((a, b) => b.score - a.score || a.entry.name.localeCompare(b.entry.name))
      .slice(0, options.limit)
      .map(({ entry }) => ({
        name: entry.name,
        catalogs: entry.meta?.catalogs ?? [],
        readOnly: entry.meta?.readOnly ?? false,
        enabled: entry.handle.enabled,
        summary: summarize(entry.handle.description),
        ...(options.includeSchema ? { inputSchema: this.inputJsonSchema(entry) } : {}),
      }));
  }

  /**
   * Proxy a call to a read-only tool without enabling its catalog. Tools that
   * modify the model are refused so the client's per-tool permission prompts
   * cannot be bypassed.
   */
  async call(name: string, args: Record<string, unknown>, extra: unknown): Promise<CallToolResult> {
    const entry = this.entries.get(name);
    if (!entry) {
      return errorResult(`Unknown tool "${name}". Use search_tools to find the right tool name.`);
    }
    if (!entry.meta?.readOnly) {
      const catalogs = entry.meta?.catalogs.join("' or '") ?? "";
      return errorResult(
        `"${name}" modifies the model and cannot be run through call_tool. ` +
          `Call enable_catalog with '${catalogs}', then call "${name}" directly. ` +
          `If it still does not appear, this client does not refresh its tool list; ask the user to set REVIT_MCP_TOOL_MODE=all.`
      );
    }

    const schema = normalizeObjectSchema(entry.handle.inputSchema);
    let parsedArgs: unknown = args;
    if (schema) {
      const parsed = await safeParseAsync(schema, args);
      if (!parsed.success) {
        return errorResult(`Invalid arguments for ${name}: ${getParseErrorMessage(parsed.error)}`);
      }
      parsedArgs = parsed.data;
    }

    if (typeof entry.handle.handler !== "function") {
      return errorResult(`"${name}" cannot be run through call_tool.`);
    }
    return await (entry.handle.handler as unknown as ToolHandler)(parsedArgs, extra);
  }

  private inputJsonSchema(entry: CatalogEntry): Record<string, unknown> {
    const schema = normalizeObjectSchema(entry.handle.inputSchema);
    return schema
      ? toJsonSchemaCompat(schema, { strictUnions: true, pipeStrategy: "input" })
      : { type: "object", properties: {} };
  }
}
