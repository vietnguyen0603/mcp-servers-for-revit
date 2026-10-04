import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { mkdtemp, rm, writeFile } from "fs/promises";
import os from "os";
import path from "path";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerCreateBeamsTool } from "../src/tools/create_beams.js";
import { registerCreateStructuralColumnsTool } from "../src/tools/create_structural_columns.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerCreateStructuralColumnsTool(server);
  registerCreateBeamsTool(server);
  return getTool;
}

let dir: string;

beforeAll(async () => {
  dir = await mkdtemp(path.join(os.tmpdir(), "framing-tools-"));
});

afterAll(async () => {
  await rm(dir, { recursive: true, force: true });
});

const okResponse = async (_command: string, params: Record<string, unknown[]>) => {
  const items = (params.columns ?? params.beams) as unknown[];
  return {
    Success: true,
    Response: {
      succeeded: items.length,
      failed: 0,
      results: items.map((_, index) => ({ index, success: true, id: 500 + index })),
    },
  };
};

describe("create_structural_columns", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockImplementation(okResponse);
  });

  it("forwards inline columns in one chunk", async () => {
    const tool = setup()("create_structural_columns");
    const columns = [
      { typeId: 42, x: 0, y: 0, baseLevel: "Level 1", topLevel: "Level 2", rotationDeg: 90, mark: "C1" },
      { familyName: "M_WWF-Welded Wide Flange-Column", typeName: "C2", x: 8000, y: 0, baseLevel: 0, height: 4200 },
    ];
    const result = await tool.invoke({ columns });
    expect(sendCommand).toHaveBeenCalledWith("create_structural_columns", { columns });
    expect(result.isError).toBeUndefined();
    expect(tool.description).toMatch(/dataFile/);
  });

  it("validates items", () => {
    const tool = setup()("create_structural_columns");
    expect(() => tool.parse({ columns: [{ x: 0, y: 0, baseLevel: 1 }] })).toThrow();
    expect(() => tool.parse({ columns: [{ typeId: 1, x: 0, baseLevel: 1 }] })).toThrow();
    expect(() => tool.parse({ columns: [{ typeId: 1, x: 0, y: 0, baseLevel: 1, height: -5 }] })).toThrow();
    expect(() => tool.parse({ columns: [{ typeId: 1, x: 0, y: 0, baseLevel: 1, unknown: 1 }] })).toThrow();
    expect(() => tool.parse({ columns: [{ typeId: 1, x: 0, y: 0, baseLevel: "L1", mark: 101 }] })).not.toThrow();
  });

  it("splits 2,000 columns from a CSV file into chunks of 300 with global indexes", async () => {
    const rows = ["typeId,x,y,baseLevel,topLevel"];
    for (let i = 0; i < 2000; i++) rows.push(`42,${i * 100},0,Level 1,Level 2`);
    const dataFile = path.join(dir, "columns.csv");
    await writeFile(dataFile, rows.join("\n"), "utf8");

    const result = await setup()("create_structural_columns").invoke({ dataFile });
    expect(sendCommand).toHaveBeenCalledTimes(7);
    const lastCall = sendCommand.mock.calls[6] as [string, { columns: unknown[] }];
    expect(lastCall[1].columns).toHaveLength(200);
    expect(lastCall[1].columns[0]).toEqual({ typeId: 42, x: 180000, y: 0, baseLevel: "Level 1", topLevel: "Level 2" });
    const parsed = JSON.parse(result.content[0].text);
    expect(parsed.Response.succeeded).toBe(2000);
    // 2,000 per-item records exceed the 60 kB limit: the result switches to the summary.
    expect(parsed.Response).toMatchObject({ summary: true, total: 2000, failures: [] });
    expect(parsed.Response.results).toBeUndefined();
    expect(parsed.Response.note).toMatch(/Per-item results omitted/);
  });

  it("rejects invalid file items before sending anything", async () => {
    const dataFile = path.join(dir, "bad-columns.json");
    await writeFile(dataFile, JSON.stringify([{ typeId: 1, x: 0, y: 0, baseLevel: 1 }, { x: 0, y: 0, baseLevel: 1 }]));
    const result = await setup()("create_structural_columns").invoke({ columns: [], dataFile });
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toMatch(/columns\[1\]/);
    expect(sendCommand).not.toHaveBeenCalled();
  });

  it("reports missing input and unreadable files as errors", async () => {
    const tool = setup()("create_structural_columns");
    expect((await tool.invoke({})).isError).toBe(true);
    const missing = await tool.invoke({ dataFile: path.join(dir, "nope.csv") });
    expect(missing.isError).toBe(true);
    expect(missing.content[0].text).toMatch(/Cannot read dataFile/);
  });
});

describe("create_beams", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockImplementation(okResponse);
  });

  it("forwards beams with offsets, justification, usage and joins", async () => {
    const tool = setup()("create_beams");
    const beams = [
      {
        typeId: 77,
        start: { x: 0, y: 0 },
        end: { x: 9000, y: 0 },
        level: "Level 2",
        startOffset: -50,
        endOffset: -50,
        zJustification: "top" as const,
        yJustification: "center" as const,
        rotationDeg: 0,
        mark: "B1",
        structuralUsage: "girder" as const,
        disallowJoin: true,
      },
      { familyName: "W-Wide Flange", typeName: "W12x26", start: { x: 0, y: 0 }, end: { x: 0, y: 6000 }, mid: { x: 500, y: 3000 }, level: 4200 },
    ];
    await tool.invoke({ beams });
    expect(sendCommand).toHaveBeenCalledWith("create_beams", { beams });
  });

  it("validates items", () => {
    const tool = setup()("create_beams");
    const base = { typeId: 1, start: { x: 0, y: 0 }, end: { x: 1, y: 0 }, level: "L1" };
    expect(() => tool.parse({ beams: [base] })).not.toThrow();
    expect(() => tool.parse({ beams: [{ ...base, typeId: undefined }] })).toThrow();
    expect(() => tool.parse({ beams: [{ ...base, zJustification: "middle" }] })).toThrow();
    expect(() => tool.parse({ beams: [{ ...base, structuralUsage: "brace" }] })).toThrow();
    expect(() => tool.parse({ beams: [{ ...base, end: { x: 1 } }] })).toThrow();
    expect(() => tool.parse({ dataFile: "C:/data/beams.csv", dataFormat: "xml" })).toThrow();
  });

  it("combines inline beams with a JSON Lines file", async () => {
    const dataFile = path.join(dir, "beams.jsonl");
    await writeFile(
      dataFile,
      ['{"typeId":7,"start":{"x":0,"y":0},"end":{"x":1000,"y":0},"level":"L2"}', '{"typeId":7,"start":{"x":0,"y":0},"end":{"x":0,"y":1000},"level":"L2"}'].join("\n")
    );
    const inline = { typeId: 8, start: { x: 0, y: 0 }, end: { x: 5, y: 5 }, level: "L3" };
    await setup()("create_beams").invoke({ beams: [inline], dataFile });
    const [, params] = sendCommand.mock.calls[0] as [string, { beams: Array<{ typeId: number }> }];
    expect(params.beams.map((b) => b.typeId)).toEqual([8, 7, 7]);
  });

  it("is an error when every beam failed", async () => {
    sendCommand.mockResolvedValueOnce({
      Success: true,
      Response: { succeeded: 0, failed: 1, results: [{ index: 0, success: false, message: "Type not found" }] },
    });
    const result = await setup()("create_beams").invoke({
      beams: [{ typeId: 1, start: { x: 0, y: 0 }, end: { x: 1, y: 0 }, level: "L1" }],
    });
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toMatch(/ERROR: all 1 of 1/);
  });

  it("registers both tools in the structure catalog", () => {
    expect(TOOL_MANIFEST.create_structural_columns.catalogs).toContain("structure");
    expect(TOOL_MANIFEST.create_beams.catalogs).toContain("structure");
  });
});
