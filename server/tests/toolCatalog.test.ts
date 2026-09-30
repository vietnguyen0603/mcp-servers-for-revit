import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";
import { describe, expect, it, vi } from "vitest";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { ToolListChangedNotificationSchema } from "@modelcontextprotocol/sdk/types.js";
import { CATALOGS, resolveCatalog } from "../src/catalog/catalogs.js";
import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { ToolCatalog, readCatalogOptions, type ToolMode } from "../src/catalog/ToolCatalog.js";
import { registerCatalogTools } from "../src/catalog/metaTools.js";

const TOOLS_DIR = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "src", "tools");

function ok(text: string) {
  return { content: [{ type: "text" as const, text }] };
}

/** Register a representative subset of real tool names with stub handlers. */
async function setup(mode: ToolMode = "dynamic", preload: string[] = []) {
  const server = new McpServer({ name: "test", version: "1.0.0" });
  const catalog = new ToolCatalog(server);
  const capturing = catalog.capturingServer();
  const beamHandler = vi.fn(async (args: unknown) => ok(JSON.stringify(args)));

  capturing.tool("get_selected_elements", "Get selected elements.", {}, async () => ok("selected"));
  capturing.tool(
    "get_beam_register_data",
    "Return register-ready beam data.",
    { limit: z.number().int().optional().default(100) },
    beamHandler
  );
  capturing.tool("tag_all_rooms", "Tag all rooms.", {}, async () => ok("tagged"));
  capturing.tool("delete_element", "Delete elements.", { elementIds: z.array(z.string()) }, async () => ok("deleted"));

  catalog.finalize({ mode, preload });
  if (mode === "dynamic") registerCatalogTools(server, catalog);

  const client = new Client({ name: "test-client", version: "1.0.0" });
  let listChanged = 0;
  client.setNotificationHandler(ToolListChangedNotificationSchema, async () => {
    listChanged++;
  });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  await Promise.all([client.connect(clientTransport), server.connect(serverTransport)]);

  const listNames = async () => (await client.listTools()).tools.map((tool) => tool.name).sort();
  const callJson = async (name: string, args: Record<string, unknown>) => {
    const result = (await client.callTool({ name, arguments: args })) as {
      content: Array<{ text: string }>;
      isError?: boolean;
    };
    return { isError: result.isError ?? false, text: result.content[0].text };
  };

  return { client, catalog, beamHandler, listNames, callJson, listChangedCount: () => listChanged };
}

describe("tool manifest", () => {
  it("covers exactly the tools registered in src/tools", () => {
    const registered = fs
      .readdirSync(TOOLS_DIR)
      .filter((file) => file.endsWith(".ts"))
      .flatMap((file) =>
        [...fs.readFileSync(path.join(TOOLS_DIR, file), "utf8").matchAll(/server\.tool\(\s*"([^"]+)"/g)].map(
          (match) => match[1]
        )
      )
      .sort();

    expect(registered).toEqual(Object.keys(TOOL_MANIFEST).sort());
  });

  it("only references defined catalogs", () => {
    const ids = new Set(CATALOGS.map((catalog) => catalog.id));
    for (const [name, entry] of Object.entries(TOOL_MANIFEST)) {
      expect(entry.catalogs.length, name).toBeGreaterThan(0);
      for (const id of entry.catalogs) {
        expect(ids.has(id), `${name} -> ${id}`).toBe(true);
      }
    }
  });

  it("resolves aliases case-insensitively", () => {
    expect(resolveCatalog("Drafting")?.id).toBe("annotate");
    expect(resolveCatalog(" struct ")?.id).toBe("structure");
    expect(resolveCatalog("arch")?.id).toBe("architecture");
    expect(resolveCatalog("mep")).toBeUndefined();
  });
});

describe("readCatalogOptions", () => {
  it("defaults to dynamic mode with no preloaded catalogs", () => {
    expect(readCatalogOptions({})).toEqual({ mode: "dynamic", preload: [] });
  });

  it("parses mode and resolves catalog aliases, skipping unknown names", () => {
    const errors = vi.spyOn(console, "error").mockImplementation(() => {});
    expect(readCatalogOptions({ REVIT_MCP_TOOL_MODE: "ALL", REVIT_MCP_CATALOGS: "struct, drafting,bogus" })).toEqual({
      mode: "all",
      preload: ["structure", "annotate"],
    });
    expect(errors).toHaveBeenCalledOnce();
  });
});

describe("ToolCatalog", () => {
  it("exposes only core and meta tools in dynamic mode", async () => {
    const { listNames } = await setup();
    expect(await listNames()).toEqual([
      "call_tool",
      "enable_catalog",
      "get_selected_elements",
      "search_tools",
    ]);
  });

  it("exposes every tool without meta tools in all mode", async () => {
    const { listNames } = await setup("all");
    expect(await listNames()).toEqual([
      "delete_element",
      "get_beam_register_data",
      "get_selected_elements",
      "tag_all_rooms",
    ]);
  });

  it("preloads catalogs and annotates read-only and destructive tools", async () => {
    const { client } = await setup("dynamic", ["structure", "modify"]);
    const tools = (await client.listTools()).tools;
    const byName = new Map(tools.map((tool) => [tool.name, tool]));

    expect(byName.get("get_beam_register_data")?.annotations).toMatchObject({ readOnlyHint: true });
    expect(byName.get("delete_element")?.annotations).toMatchObject({ readOnlyHint: false, destructiveHint: true });
    expect(byName.has("tag_all_rooms")).toBe(false);
  });

  it("enables a catalog by alias and notifies the client once", async () => {
    const { listNames, callJson, listChangedCount } = await setup();

    const result = await callJson("enable_catalog", { catalogs: ["drafting", "annotate"] });
    expect(result.isError).toBe(false);
    expect(JSON.parse(result.text)).toMatchObject({ catalogs: ["annotate"], enabled: ["tag_all_rooms"] });

    await vi.waitFor(() => expect(listChangedCount()).toBe(1));
    expect(await listNames()).toContain("tag_all_rooms");
  });

  it("rejects unknown catalogs", async () => {
    const { callJson } = await setup();
    const result = await callJson("enable_catalog", { catalogs: ["mep"] });
    expect(result.isError).toBe(true);
    expect(result.text).toContain("Valid catalogs");
  });

  it("searches by keyword and filters by catalog alias", async () => {
    const { callJson } = await setup();

    const byKeyword = JSON.parse((await callJson("search_tools", { query: "beam span" })).text);
    expect(byKeyword.matches[0]).toMatchObject({
      name: "get_beam_register_data",
      catalogs: ["structure"],
      readOnly: true,
      enabled: false,
    });
    expect(byKeyword.matches[0].inputSchema).toBeUndefined();

    const byCatalog = JSON.parse((await callJson("search_tools", { catalog: "drafting", includeSchema: true })).text);
    expect(byCatalog.matches.map((match: { name: string }) => match.name)).toEqual(["tag_all_rooms"]);
    expect(byCatalog.matches[0].inputSchema).toMatchObject({ type: "object" });
  });

  it("proxies read-only tools through call_tool with validated arguments", async () => {
    const { callJson, beamHandler } = await setup();

    const result = await callJson("call_tool", { name: "get_beam_register_data" });
    expect(result.isError).toBe(false);
    expect(JSON.parse(result.text)).toEqual({ limit: 100 });
    expect(beamHandler).toHaveBeenCalledOnce();

    const invalid = await callJson("call_tool", { name: "get_beam_register_data", arguments: { limit: "x" } });
    expect(invalid.isError).toBe(true);
    expect(invalid.text).toContain("Invalid arguments");
    expect(beamHandler).toHaveBeenCalledOnce();
  });

  it("refuses to proxy tools that modify the model, even once enabled", async () => {
    const { callJson } = await setup("dynamic", ["modify"]);

    const result = await callJson("call_tool", { name: "delete_element", arguments: { elementIds: ["1"] } });
    expect(result.isError).toBe(true);
    expect(result.text).toContain("enable_catalog");

    const unknown = await callJson("call_tool", { name: "not_a_tool" });
    expect(unknown.isError).toBe(true);
    expect(unknown.text).toContain("search_tools");
  });
});
