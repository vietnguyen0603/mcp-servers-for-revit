import { mkdtempSync, writeFileSync } from "fs";
import { tmpdir } from "os";
import path from "path";
import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { registerCreateOpeningsTool } from "../src/tools/create_openings.js";
import { registerCreateStairsTool } from "../src/tools/create_stairs.js";
import { registerCreateSlabsTool } from "../src/tools/create_slabs.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerCreateOpeningsTool(server);
  registerCreateStairsTool(server);
  registerCreateSlabsTool(server);
  return getTool;
}

/** Revit stub: echoes one success result per item (local indexes). */
function echoBatch() {
  sendCommand.mockImplementation(async (_command: string, params: Record<string, unknown[]>) => {
    const items = (params.openings ?? params.stairs ?? params.slabs) as unknown[];
    return {
      Success: true,
      Message: "ok",
      Response: {
        succeeded: items.length,
        failed: 0,
        results: items.map((_, index) => ({ index, success: true, id: 2000 + index })),
      },
    };
  });
}

const square = (x0: number, y0: number, size: number) => [
  { x: x0, y: y0 },
  { x: x0 + size, y: y0 },
  { x: x0 + size, y: y0 + size },
  { x: x0, y: y0 + size },
];

function tempFile(name: string, content: string) {
  const dir = mkdtempSync(path.join(tmpdir(), "bulk-"));
  const file = path.join(dir, name);
  writeFileSync(file, content, "utf8");
  return file;
}

describe("create_openings", () => {
  beforeEach(() => {
    resetConnectionMock();
    echoBatch();
  });

  it("forwards shaft, wall and floor openings", async () => {
    const tool = setup()("create_openings");
    const openings = [
      { kind: "shaft", boundary: square(0, 0, 2000), baseLevel: "B3", baseOffset: -500, topLevel: "Roof", topOffset: 1200 },
      { kind: "wall", wallId: 123, width: 900, height: 2100, sillHeight: 0 },
      { kind: "wall", point: { x: 1000, y: 0 }, level: "L5", width: 1200, height: 1500, sillHeight: 900 },
      { kind: "floor", boundary: square(100, 100, 500), floorId: 77 },
      { kind: "floor", boundary: square(100, 100, 500), level: "L2", point: { x: 300, y: 300 }, perpendicular: false },
    ];
    const result = await tool.invoke({ openings });
    expect(sendCommand).toHaveBeenCalledWith("create_openings", { openings });
    expect(result.isError).toBeUndefined();
    expect(result.content[0].text).toContain('"succeeded": 5');
  });

  it("rejects incomplete opening items", () => {
    const tool = setup()("create_openings");
    const bad = [
      { kind: "shaft", boundary: square(0, 0, 1000), baseLevel: "L1" },
      { kind: "shaft", boundary: square(0, 0, 1000).slice(0, 2), baseLevel: "L1", topLevel: "L2" },
      { kind: "wall", point: { x: 0, y: 0 }, width: 900, height: 2100 },
      { kind: "wall", wallId: 1, width: 900 },
      { kind: "wall", wallId: 1, width: -900, height: 2100 },
      { kind: "floor", boundary: square(0, 0, 1000) },
      { kind: "floor", boundary: square(0, 0, 1000), floorId: 5, level: "L1" },
      { kind: "floor", boundary: square(0, 0, 1000), floorId: 5, point: { x: 1, y: 1 } },
      { kind: "roof", boundary: square(0, 0, 1000) },
      { kind: "floor", boundary: square(0, 0, 1000), floorId: 5, colour: "red" },
    ];
    for (const item of bad) {
      expect(() => tool.parse({ openings: [item] }), JSON.stringify(item)).toThrow();
    }
  });

  it("reads openings from CSV with JSON boundary cells and dotted point headers", async () => {
    const tool = setup()("create_openings");
    const csv =
      'kind,wallId,point.x,point.y,level,width,height,sillHeight,boundary\n' +
      "wall,,500,0,L3,900,2100,0,\n" +
      'floor,,,,L3,,,,"[{""x"":0,""y"":0},{""x"":800,""y"":0},{""x"":800,""y"":800}]"\n';
    await tool.invoke({ dataFile: tempFile("openings.csv", csv) });
    const sent = (sendCommand.mock.calls[0][1] as { openings: Array<Record<string, unknown>> }).openings;
    expect(sent[0]).toEqual({ kind: "wall", point: { x: 500, y: 0 }, level: "L3", width: 900, height: 2100, sillHeight: 0 });
    expect(sent[1]).toEqual({ kind: "floor", level: "L3", boundary: [{ x: 0, y: 0 }, { x: 800, y: 0 }, { x: 800, y: 800 }] });
  });
});

describe("create_stairs", () => {
  beforeEach(() => {
    resetConnectionMock();
    echoBatch();
  });

  it("forwards single and U-shaped stairs", async () => {
    const tool = setup()("create_stairs");
    const stairs = [
      { baseLevel: "L1", topLevel: "L2", start: { x: 0, y: 0 }, angleDeg: 90, width: 1200 },
      {
        baseLevel: "L2",
        topLevel: "L3",
        topOffset: -50,
        start: { x: 0, y: 0 },
        direction: { x: 1, y: 0 },
        width: 1200,
        flights: 2,
        gap: 150,
        side: "left",
        landingDepth: 1400,
        riserCount: 20,
        treadDepth: 280,
        stairsTypeName: "Cast-In-Place Stair",
      },
    ];
    const result = await tool.invoke({ stairs });
    expect(sendCommand).toHaveBeenCalledWith("create_stairs", { stairs });
    expect(result.isError).toBeUndefined();
  });

  it("rejects bad stair items", () => {
    const tool = setup()("create_stairs");
    const base = { baseLevel: "L1", topLevel: "L2", start: { x: 0, y: 0 }, width: 1200 };
    const bad = [
      base,
      { ...base, angleDeg: 0, direction: { x: 1, y: 0 } },
      { ...base, direction: { x: 0, y: 0 } },
      { ...base, angleDeg: 0, flights: 3 },
      { ...base, angleDeg: 0, gap: 100 },
      { ...base, angleDeg: 0, flights: 2, side: "up" },
      { ...base, angleDeg: 0, riserCount: 1 },
      { ...base, angleDeg: 0, stairsTypeId: 5, stairsTypeName: "A" },
      { baseLevel: "L1", topLevel: "L2", start: { x: 0, y: 0 }, angleDeg: 0 },
    ];
    for (const item of bad) {
      expect(() => tool.parse({ stairs: [item] }), JSON.stringify(item)).toThrow();
    }
  });
});

describe("create_slabs zones and drop panels", () => {
  beforeEach(() => {
    resetConnectionMock();
    echoBatch();
  });

  it("forwards zones and drop panels", async () => {
    const tool = setup()("create_slabs");
    const slab = {
      level: "L10",
      boundary: square(0, 0, 20000),
      thickness: 250,
      zones: [
        { boundary: square(1000, 1000, 2000), thickness: 450 },
        { boundary: square(5000, 5000, 2000), typeName: "PT-250", offset: -50, mark: "R1" },
      ],
      dropPanels: [{ boundary: square(9000, 9000, 2500), depth: 200 }],
    };
    const result = await tool.invoke({ slabs: [slab] });
    expect(sendCommand).toHaveBeenCalledWith("create_slabs", { slabs: [slab] });
    expect(result.isError).toBeUndefined();
  });

  it("rejects zones without exactly one type source and bad drop panels", () => {
    const tool = setup()("create_slabs");
    const base = { level: "L1", boundary: square(0, 0, 10000) };
    const bad = [
      { ...base, zones: [{ boundary: square(1000, 1000, 1000) }] },
      { ...base, zones: [{ boundary: square(1000, 1000, 1000), thickness: 300, typeId: 4 }] },
      { ...base, dropPanels: [{ boundary: square(1000, 1000, 1000) }] },
      { ...base, dropPanels: [{ boundary: square(1000, 1000, 1000), depth: 0 }] },
      { ...base, dropPanels: [{ boundary: square(1000, 1000, 1000), depth: 200, typeId: 1, typeName: "X" }] },
    ];
    for (const item of bad) {
      expect(() => tool.parse({ slabs: [item] }), JSON.stringify(item)).toThrow();
    }
  });

  it("decodes zones and dropPanels JSON cells from CSV", async () => {
    const tool = setup()("create_slabs");
    const zones = JSON.stringify([{ boundary: square(100, 100, 200), thickness: 450 }]).replace(/"/g, '""');
    const drops = JSON.stringify([{ boundary: square(500, 500, 200), depth: 150 }]).replace(/"/g, '""');
    const boundary = JSON.stringify(square(0, 0, 1000)).replace(/"/g, '""');
    const csv = `level,thickness,boundary,zones,dropPanels\nL1,250,"${boundary}","${zones}","${drops}"\n`;
    await tool.invoke({ dataFile: tempFile("slabs.csv", csv) });
    const sent = (sendCommand.mock.calls[0][1] as { slabs: Array<Record<string, unknown>> }).slabs[0];
    expect(sent.zones).toEqual([{ boundary: square(100, 100, 200), thickness: 450 }]);
    expect(sent.dropPanels).toEqual([{ boundary: square(500, 500, 200), depth: 150 }]);
  });
});
