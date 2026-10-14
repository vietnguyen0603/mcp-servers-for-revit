import { beforeEach, describe, expect, it, vi } from "vitest";
import Database from "better-sqlite3";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

// Keep the tool's SQLite index in memory, away from the real revit-data.db
vi.mock("../src/database/db.js", async () => {
  const { default: Sqlite } = await import("better-sqlite3");
  return { db: new Sqlite(":memory:") };
});

import { registerGetDraftingStandardsTool } from "../src/tools/get_drafting_standards.js";
import { registerAuditDetailLibraryTool } from "../src/tools/audit_detail_library.js";
import { registerSearchDetailLibraryTool } from "../src/tools/search_detail_library.js";
import {
  disableFtsForTesting,
  ensureDetailIndexSchema,
  replaceDetailIndex,
  searchDetailIndex,
  tokenizeQuery,
  type IndexedView,
} from "../src/database/detailLibraryIndex.js";
import { renderStandardsMarkdown } from "../src/utils/draftingLibrary.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerGetDraftingStandardsTool(server);
  registerAuditDetailLibraryTool(server);
  registerSearchDetailLibraryTool(server);
  return getTool;
}

const LIBRARY: IndexedView[] = [
  {
    id: 101,
    name: "ST-BM-105-WR_STL-TYP-COL Beam to WF Col Flange Connection",
    scale: 12,
    sheetNumber: "S5-501",
    detailNumber: "3",
    sheetName: "TYPICAL STEEL DETAILS",
    text: ["SHEAR TAB PL 3/8\"", "(3) 3/4\" A325 BOLTS", "WF COLUMN"],
    families: [{ name: "_TT-DC-Fastener-Bolt Head : 7/8\" HEX", category: "Detail Items", count: 3 }],
  },
  {
    id: 102,
    name: "ST-BM-110-WR_STL-TYP HSS Beam Connection",
    scale: 12,
    sheetNumber: "S5-502",
    detailNumber: "1",
    text: ["HSS 6x6x3/8", "SHEAR TAB PL 1/2\" EACH SIDE"],
    families: [{ name: "TT-AISC Angle Shapes-Section : L3X3X3/8", count: 1 }],
  },
  {
    id: 103,
    name: "FN-SF-03-WR_CONC-TYP Construction Joints",
    scale: 12,
    text: ["#5 @ 12\" OC", "KEYWAY"],
    families: [{ name: "Reinf Bar Section : #5", count: 8 }],
  },
];

function payload(body: unknown) {
  return { Success: true, Message: "ok", Response: body };
}

describe("drafting library tools", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue(payload({}));
  });

  it("dispatches get_drafting_standards and renders the snapshot markdown", async () => {
    const tool = setup()("get_drafting_standards");
    sendCommand.mockResolvedValue(
      payload({
        documentTitle: "TT-LIB",
        generatedAt: "2026-10-14",
        views: 2,
        draftingViewsOnly: true,
        sections: {
          scales: [{ scale: 12, label: "1:12", views: 2 }],
          lineStyles: {
            total: 2,
            truncated: true,
            items: [{ name: "PEN3", uses: 10, weight: 3, color: "#000000", pattern: "Solid" }],
          },
          roles: { breakLine: [{ family: "TT-Break Line", type: "TT-Break Line", uses: 4, category: "Detail Items", placement: "point" }] },
          viewNames: { total: 2, pattern: "^X", matching: 1, prefixes: [{ prefix: "FN", views: 2 }], samples: ["FN-1_A"] },
        },
      })
    );

    const result = await tool.invoke({ sections: ["scales", "lineStyles", "roles", "viewNames"], top: 1 });
    expect(sendCommand).toHaveBeenCalledWith(
      "get_drafting_standards",
      { sections: ["scales", "lineStyles", "roles", "viewNames"], top: 1, includeUnused: false, viewNameSamples: 16 },
      190000
    );
    const text = result.content[0].text;
    expect(result.isError).toBeUndefined();
    expect(text).toContain("# Drafting standards snapshot: TT-LIB");
    expect(text).toContain("1:12 (2)");
    expect(text).toContain("| PEN3 | 10 | 3 | #000000 | Solid |");
    expect(text).toContain("(1 of 2 shown)");
    expect(text).toContain("| breakLine | TT-Break Line : TT-Break Line | 4 |");
    expect(text).toContain("View names matching `^X`: 1 of 2");

    const json = await tool.invoke({ format: "json", sections: ["scales"] });
    expect(JSON.parse(json.content[0].text).Response.documentTitle).toBe("TT-LIB");

    expect(() => tool.parse({ sections: ["pens"] })).toThrow();
    expect(() => tool.parse({ top: 0 })).toThrow();
  });

  it("flags a failed standards harvest as an error", async () => {
    const tool = setup()("get_drafting_standards");
    sendCommand.mockResolvedValue({ Success: false, Message: "Unknown sections" });
    const result = await tool.invoke({});
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toContain("Unknown sections");
  });

  it("renders markdown for empty sections without throwing", () => {
    expect(renderStandardsMarkdown({ documentTitle: "X", sections: {} })).toContain("snapshot: X");
  });

  it("dispatches audit_detail_library with standards and can strip detail rows", async () => {
    const tool = setup()("audit_detail_library");
    sendCommand.mockResolvedValue(
      payload({
        summary: { placement: 1 },
        details: { placement: { count: 1, items: [{ viewId: 5, viewName: "A" }], truncated: false } },
      })
    );

    const args = {
      checks: ["placement", "lineStyles", "references"],
      sheetNumbers: ["S5-501"],
      standards: { lineStyles: ["PEN*", "/^S-DETL-/", "<Thin Lines>"], minUses: 3 },
      placeholders: ["", "-"],
    };
    const full = await tool.invoke(args);
    expect(sendCommand).toHaveBeenCalledWith(
      "audit_detail_library",
      { ...args, limitPerCheck: 25 },
      190000
    );
    expect(full.content[0].text).toContain('"viewName": "A"');

    const compact = await tool.invoke({ summaryOnly: true });
    expect(compact.content[0].text).not.toContain('"items"');
    expect(compact.content[0].text).toContain('"placement": 1');

    expect(() => tool.parse({ checks: ["everything"] })).toThrow();
    expect(() => tool.parse({ standards: { lineStyles: [] } })).toThrow();
    expect(() => tool.parse({ standards: { colors: ["red"] } })).toThrow();
    expect(() => tool.parse({ limitPerCheck: 0 })).toThrow();
    expect(() => tool.parse({ viewIds: [0] })).toThrow();
  });

  it("indexes the active document on first search, then searches without re-indexing", async () => {
    const tool = setup()("search_detail_library");
    sendCommand.mockImplementation(async (_command: string, params: { offset: number; limit: number }) =>
      payload({
        documentTitle: "TT-LIB-A",
        documentPath: "C:\\lib.rvt",
        total: LIBRARY.length,
        offset: params.offset,
        returned: params.limit === 0 ? 0 : LIBRARY.length,
        truncated: params.limit === 0,
        views: params.limit === 0 ? [] : LIBRARY.slice(params.offset, params.offset + params.limit),
      })
    );

    const first = JSON.parse((await tool.invoke({ query: "HSS shear tab" })).content[0].text);
    expect(sendCommand).toHaveBeenCalledWith("get_drafting_view_index", { offset: 0, limit: 0 }, 190000);
    expect(sendCommand).toHaveBeenCalledWith("get_drafting_view_index", { offset: 0, limit: 200 }, 190000);
    expect(first.indexedNow).toBe(true);
    expect(first.viewCount).toBe(3);
    expect(first.results[0].viewId).toBe(102);
    expect(first.results[0].matchedTerms).toEqual(["hss", "shear", "tab"]);
    expect(first.results[0].snippets[0]).toContain("HSS");
    expect(first.results.map((r: { viewId: number }) => r.viewId)).toContain(101);
    expect(first.results.map((r: { viewId: number }) => r.viewId)).not.toContain(103);

    sendCommand.mockClear();
    const second = JSON.parse((await tool.invoke({ query: "3/S5-501" })).content[0].text);
    expect(sendCommand).toHaveBeenCalledTimes(1); // header only, count unchanged
    expect(second.indexedNow).toBe(false);
    expect(second.results[0].viewId).toBe(101);
    expect(second.results[0].matchedIn).toContain("sheet");

    sendCommand.mockClear();
    const offline = JSON.parse((await tool.invoke({ query: "keyway reinf", documentTitle: "TT-LIB-A" })).content[0].text);
    expect(sendCommand).not.toHaveBeenCalled();
    expect(offline.results[0].viewId).toBe(103);
    expect(offline.results[0].families[0]).toContain("Reinf Bar Section");

    const missing = await tool.invoke({ query: "x", documentTitle: "OTHER" });
    expect(missing.isError).toBe(true);
    expect(missing.content[0].text).toContain("TT-LIB-A");

    sendCommand.mockClear();
    await tool.invoke({ query: "bolt", refresh: true });
    expect(sendCommand).toHaveBeenCalledTimes(2);

    expect(() => tool.parse({ query: "  " })).toThrow();
    expect(() => tool.parse({ query: "a", limit: 0 })).toThrow();
  });

  it("reports a Revit failure while indexing", async () => {
    const tool = setup()("search_detail_library");
    sendCommand.mockResolvedValue({ Success: false, Message: "No active Revit document." });
    const result = await tool.invoke({ query: "tab" });
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toContain("No active Revit document.");
  });
});

describe("detail library index", () => {
  it("tokenizes terms and quoted phrases", () => {
    expect(tokenizeQuery('HSS "shear tab" hss  #5')).toEqual(["hss", "shear tab", "5"]);
    expect(tokenizeQuery("  ")).toEqual([]);
  });

  for (const engine of ["fts5", "like"] as const) {
    it(`ranks the same way with the ${engine} engine and keeps documents apart`, () => {
      const db = new Database(":memory:");
      if (engine === "like") disableFtsForTesting(db);
      else expect(ensureDetailIndexSchema(db)).toBe(true);

      replaceDetailIndex(db, { title: "A" }, LIBRARY);
      replaceDetailIndex(db, { title: "B" }, [{ id: 1, name: "HSS shear tab elsewhere" }]);

      const result = searchDetailIndex(db, "A", '"shear tab" hss', 10);
      expect(result.engine).toBe(engine);
      expect(result.results.map((r) => r.viewId)).toEqual([102, 101]);
      expect(result.results[1].matchedTerms).toEqual(["shear tab"]);

      // Prefix matching at word starts: "constr" finds "Construction", "ab" finds nothing inside "tab"
      expect(searchDetailIndex(db, "A", "constr", 10).results[0].viewId).toBe(103);
      expect(searchDetailIndex(db, "A", "ab", 10).total).toBe(0);

      // Re-indexing replaces rows instead of duplicating them
      replaceDetailIndex(db, { title: "A" }, LIBRARY.slice(0, 1));
      expect(searchDetailIndex(db, "A", "shear", 10).results.map((r) => r.viewId)).toEqual([101]);
      expect(searchDetailIndex(db, "B", "shear", 10).results.map((r) => r.viewId)).toEqual([1]);
    });
  }
});
