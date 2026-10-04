import { mkdtempSync, writeFileSync } from "fs";
import { tmpdir } from "os";
import path from "path";
import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerCreateSlabsTool } from "../src/tools/create_slabs.js";
import { registerCreateFoundationsTool } from "../src/tools/create_foundations.js";
import { loadItems, sendInChunks } from "../src/utils/bulkInput.js";
import { decodeJsonFields } from "../src/utils/bulkCommand.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerCreateSlabsTool(server);
  registerCreateFoundationsTool(server);
  return getTool;
}

/** Revit stub: echoes one success result per item (local indexes). */
function echoBatch() {
  sendCommand.mockImplementation(async (_command: string, params: Record<string, unknown[]>) => {
    const items = (params.slabs ?? params.foundations) as unknown[];
    return {
      Success: true,
      Message: "ok",
      Response: {
        succeeded: items.length,
        failed: 0,
        results: items.map((_, index) => ({ index, success: true, id: 1000 + index })),
      },
    };
  });
}

const square = [
  { x: 0, y: 0 },
  { x: 6000, y: 0 },
  { x: 6000, y: 6000 },
  { x: 0, y: 6000 },
];

function tempFile(name: string, content: string) {
  const dir = mkdtempSync(path.join(tmpdir(), "bulk-"));
  const file = path.join(dir, name);
  writeFileSync(file, content, "utf8");
  return file;
}

describe("create_slabs", () => {
  beforeEach(() => {
    resetConnectionMock();
    echoBatch();
  });

  it("forwards slabs with openings, offsets and thickness", async () => {
    const tool = setup()("create_slabs");
    const slab = {
      level: "L1",
      offset: -1100,
      boundary: square,
      openings: [[{ x: 1000, y: 1000 }, { x: 2000, y: 1000 }, { x: 2000, y: 2000 }]],
      thickness: 200,
      mark: "S1",
    };
    const result = await tool.invoke({ slabs: [slab] });
    expect(sendCommand).toHaveBeenCalledWith("create_slabs", { slabs: [slab] });
    expect(result.isError).toBeUndefined();
    expect(result.content[0].text).toContain('"succeeded": 1');
  });

  it("accepts segment boundaries, foundation slabs, slope arrows and level elevations", () => {
    const tool = setup()("create_slabs");
    const slab = {
      level: -3500,
      boundarySegments: [
        { start: { x: 0, y: 0 }, end: { x: 4000, y: 0 } },
        { start: { x: 4000, y: 0 }, end: { x: 0, y: 0 }, mid: { x: 2000, y: 2000 } },
      ],
      foundation: true,
      typeId: 123,
      slopeArrow: { start: { x: 0, y: 0 }, end: { x: 4000, y: 0 }, riseMm: -50 },
    };
    expect(() => tool.parse({ slabs: [slab] })).not.toThrow();
  });

  it("rejects bad slab items", () => {
    const tool = setup()("create_slabs");
    expect(() => tool.parse({ slabs: [{ level: "L1" }] })).toThrow();
    expect(() => tool.parse({ slabs: [{ level: "L1", boundary: square.slice(0, 2) }] })).toThrow();
    expect(() => tool.parse({ slabs: [{ level: "L1", boundary: square, typeId: 1, thickness: 200 }] })).toThrow();
    expect(() => tool.parse({ slabs: [{ level: "L1", boundary: square, colour: "red" }] })).toThrow();
  });

  it("reads slabs from a CSV file with JSON boundary cells", async () => {
    const tool = setup()("create_slabs");
    const csv = 'level,offset,thickness,boundary\nL1,-50,250,"[{""x"":0,""y"":0},{""x"":1000,""y"":0},{""x"":1000,""y"":1000}]"\n';
    const file = tempFile("slabs.csv", csv);
    await tool.invoke({ dataFile: file });
    const params = sendCommand.mock.calls[0][1] as { slabs: Array<Record<string, unknown>> };
    expect(params.slabs[0]).toEqual({
      level: "L1",
      offset: -50,
      thickness: 250,
      boundary: [{ x: 0, y: 0 }, { x: 1000, y: 0 }, { x: 1000, y: 1000 }],
    });
  });

  it("reports invalid file items without calling Revit", async () => {
    const tool = setup()("create_slabs");
    const file = tempFile("slabs.json", JSON.stringify({ slabs: [{ level: "L1", boundary: square }, { level: "L1" }] }));
    const result = await tool.invoke({ dataFile: file });
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toContain("slabs[1]");
    expect(sendCommand).not.toHaveBeenCalled();
  });

  it("errors when no slabs are given", async () => {
    const result = await setup()("create_slabs").invoke({});
    expect(result.isError).toBe(true);
  });
});

describe("create_foundations", () => {
  beforeEach(() => {
    resetConnectionMock();
    echoBatch();
  });

  it("validates isolated, pile and capSlab items", () => {
    const tool = setup()("create_foundations");
    const items = [
      { kind: "isolated", familyName: "M_Footing-Rectangular", typeName: "F1", x: 0, y: 0, level: "B1", topOffset: -200, rotationDeg: 30, mark: "F1" },
      { kind: "capSlab", boundary: square, level: "B1", thickness: 1200, mark: "PC-H1" },
      { kind: "pile", typeId: 55, x: 100, y: 100, underFoundationMark: "PC-H1", length: 12000 },
      { kind: "pile", familyName: "Pile D400", x: 500, y: 100, topElevation: -5000, length: 9000, baseLevel: "B2" },
    ];
    expect(() => tool.parse({ foundations: items })).not.toThrow();
  });

  it("rejects incomplete foundation items", () => {
    const tool = setup()("create_foundations");
    const bad = [
      { kind: "pile", typeId: 55, x: 0, y: 0, length: 9000 },
      { kind: "pile", typeId: 55, x: 0, y: 0, length: 9000, topElevation: 0, underFoundationId: 9 },
      { kind: "pile", x: 0, y: 0, length: 9000, topElevation: 0 },
      { kind: "isolated", x: 0, y: 0, level: "B1" },
      { kind: "capSlab", boundary: square, level: "B1" },
      { kind: "capSlab", boundary: square, level: "B1", thickness: 1000, typeId: 4 },
      { kind: "raft", x: 0, y: 0 },
    ];
    for (const item of bad) {
      expect(() => tool.parse({ foundations: [item] }), JSON.stringify(item)).toThrow();
    }
  });

  it("sends 700 piles in chunks of 300 with global result indexes", async () => {
    const tool = setup()("create_foundations");
    const piles = Array.from({ length: 700 }, (_, i) => ({
      kind: "pile",
      typeId: 55,
      x: i * 1000,
      y: 0,
      underFoundationMark: `PC${Math.floor(i / 4)}`,
      length: i % 2 ? 9000 : 12000,
    }));
    const result = await tool.invoke({ foundations: piles });
    expect(sendCommand).toHaveBeenCalledTimes(3);
    expect((sendCommand.mock.calls[2][1] as { foundations: unknown[] }).foundations).toHaveLength(100);
    const parsed = JSON.parse(result.content[0].text.slice(result.content[0].text.indexOf("{")));
    const outcome = parsed.Response ?? parsed;
    expect(outcome.succeeded).toBe(700);
    expect(outcome.results[650].index).toBe(650);
    expect(result.isError).toBeUndefined();
  });

  it("reads piles from CSV and JSON Lines files", async () => {
    const tool = setup()("create_foundations");
    const csv = tempFile("piles.csv", "kind,typeId,x,y,underFoundationMark,length\npile,55,100,200,PC1,12000\npile,55,300,200,PC1,9000\n");
    await tool.invoke({ dataFile: csv });
    const sent = (sendCommand.mock.calls[0][1] as { foundations: Array<Record<string, unknown>> }).foundations;
    expect(sent).toHaveLength(2);
    expect(sent[1]).toEqual({ kind: "pile", typeId: 55, x: 300, y: 200, underFoundationMark: "PC1", length: 9000 });

    const jsonl = tempFile("caps.jsonl", JSON.stringify({ kind: "capSlab", boundary: square, level: "B1", thickness: 1500 }) + "\n");
    await tool.invoke({ dataFile: jsonl });
    expect((sendCommand.mock.calls[1][1] as { foundations: unknown[] }).foundations).toHaveLength(1);
  });

  it("is an error only when nothing succeeded", async () => {
    const tool = setup()("create_foundations");
    sendCommand.mockResolvedValue({
      Success: true,
      Message: "ok",
      Response: { succeeded: 0, failed: 1, results: [{ index: 0, success: false, message: "Family not loaded" }] },
    });
    const result = await tool.invoke({ foundations: [{ kind: "pile", typeId: 55, x: 0, y: 0, topElevation: 0, length: 9000 }] });
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toContain("Family not loaded");
  });

  it("registers both tools in the catalogs", () => {
    expect(TOOL_MANIFEST.create_slabs.catalogs).toEqual(expect.arrayContaining(["structure", "architecture"]));
    expect(TOOL_MANIFEST.create_foundations.catalogs).toContain("structure");
  });
});

describe("bulk input helpers", () => {
  beforeEach(() => {
    resetConnectionMock();
    echoBatch();
  });

  it("puts inline items before file items and nests dotted CSV headers", async () => {
    const file = tempFile("items.csv", "kind,start.x,start.y,note\npile,1.5,-2,\"a, b\"\n");
    const items = await loadItems<Record<string, unknown>>({ items: [{ kind: "first" }], dataFile: file }, "foundations");
    expect(items).toEqual([{ kind: "first" }, { kind: "pile", start: { x: 1.5, y: -2 }, note: "a, b" }]);
  });

  it("requires an absolute dataFile path", async () => {
    await expect(loadItems({ dataFile: "relative/items.json" }, "slabs")).rejects.toThrow(/absolute/);
  });

  it("marks a failing chunk's items failed", async () => {
    sendCommand.mockReset();
    sendCommand.mockRejectedValueOnce(new Error("socket closed"));
    const outcome = await sendInChunks("create_foundations", {}, "foundations", [1, 2, 3], 2);
    expect(outcome.failed).toBe(3);
    expect(outcome.succeeded).toBe(0);
    expect((outcome.results[0] as { message: string }).message).toContain("socket closed");
  });

  it("decodes JSON cells only for the listed fields", () => {
    expect(decodeJsonFields({ boundary: "[1,2]", mark: "[A]" }, ["boundary"])).toEqual({ boundary: [1, 2], mark: "[A]" });
  });
});
