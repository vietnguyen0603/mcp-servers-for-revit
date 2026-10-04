import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { mkdtemp, rm, writeFile } from "fs/promises";
import os from "os";
import path from "path";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { registerCreateWallsTool, wallItemSchema } from "../src/tools/create_walls.js";
import { registerCreateFamilyTypeTool } from "../src/tools/create_family_type.js";
import { registerCreateBeamsTool } from "../src/tools/create_beams.js";
import { registerSaveDocumentTool, saveDocumentSchema } from "../src/tools/save_document.js";
import { openDocumentSchema, registerOpenDocumentTool } from "../src/tools/open_document.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerCreateWallsTool(server);
  registerCreateFamilyTypeTool(server);
  registerCreateBeamsTool(server);
  registerSaveDocumentTool(server);
  registerOpenDocumentTool(server);
  return getTool;
}

/** Revit stub: one success result per item of whichever array was sent. */
function echoBatch() {
  sendCommand.mockImplementation(async (_command: string, params: Record<string, unknown>) => {
    const items = (params.walls ?? params.types ?? params.beams) as unknown[];
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

let dir: string;

beforeAll(async () => {
  dir = await mkdtemp(path.join(os.tmpdir(), "walls-docs-"));
});

afterAll(async () => {
  await rm(dir, { recursive: true, force: true });
});

const wall = { start: { x: 0, y: 0 }, end: { x: 6000, y: 0 }, baseLevel: "L1", topLevel: "L2" };

describe("create_walls", () => {
  beforeEach(() => {
    resetConnectionMock();
    echoBatch();
  });

  it("accepts level-to-level, unconnected, arc and diaphragm walls", () => {
    expect(wallItemSchema.safeParse(wall).success).toBe(true);
    expect(wallItemSchema.safeParse({ ...wall, topLevel: undefined, height: 3000, thickness: 250 }).success).toBe(true);
    expect(
      wallItemSchema.safeParse({ ...wall, mid: { x: 3000, y: 1000 }, typeName: "Wall 300mm", locationLine: "finishFaceExterior", flip: true }).success
    ).toBe(true);
    expect(
      wallItemSchema.safeParse({ ...wall, baseLevel: "B1", baseOffset: -37500, topLevel: -1500, topOffset: 0, thickness: 800, structural: true, mark: "DW1" }).success
    ).toBe(true);
  });

  it("rejects ambiguous types, tops and unknown fields", () => {
    expect(wallItemSchema.safeParse({ ...wall, typeId: 1, thickness: 200 }).success).toBe(false);
    expect(wallItemSchema.safeParse({ ...wall, height: 3000 }).success).toBe(false);
    expect(wallItemSchema.safeParse({ ...wall, topLevel: undefined }).success).toBe(false);
    expect(wallItemSchema.safeParse({ ...wall, topLevel: undefined, height: 3000, topOffset: 10 }).success).toBe(false);
    expect(wallItemSchema.safeParse({ ...wall, locationLine: "exterior" }).success).toBe(false);
    expect(wallItemSchema.safeParse({ ...wall, thickness: 0 }).success).toBe(false);
    expect(wallItemSchema.safeParse({ ...wall, level: "L1" }).success).toBe(false);
  });

  it("forwards walls with the type prefix and reads a CSV data file in chunks", async () => {
    const tool = setup()("create_walls");
    const csv = path.join(dir, "walls.csv");
    const rows = ["start.x,start.y,end.x,end.y,baseLevel,topLevel,thickness,typeName"];
    for (let i = 0; i < 301; i++) rows.push(`0,${i * 100},5000,${i * 100},L1,L2,300,`);
    await writeFile(csv, rows.join("\n"), "utf8");

    const result = await tool.invoke({ walls: [wall], dataFile: csv, typeNamePrefix: "RC Wall", summary: true });
    expect(sendCommand).toHaveBeenCalledTimes(2);
    expect(sendCommand.mock.calls[0][1]).toMatchObject({ typeNamePrefix: "RC Wall", walls: expect.any(Array) });
    expect(sendCommand.mock.calls[0][1].walls[0]).toEqual(wall);
    expect(sendCommand.mock.calls[0][1].walls[1]).toEqual({
      start: { x: 0, y: 0 },
      end: { x: 5000, y: 0 },
      baseLevel: "L1",
      topLevel: "L2",
      thickness: 300,
    });
    expect(sendCommand.mock.calls[1][1].walls).toHaveLength(2);
    const body = JSON.parse(result.content[0].text);
    expect(body.Response).toMatchObject({ summary: true, total: 302, createdIds: { count: 302 } });
  });

  it("keeps a numeric CSV type name as a string", async () => {
    const tool = setup()("create_walls");
    const csv = path.join(dir, "named.csv");
    await writeFile(csv, "start.x,start.y,end.x,end.y,baseLevel,height,typeName\n0,0,1000,0,L1,3000,300\n", "utf8");
    await tool.invoke({ dataFile: csv });
    expect(sendCommand.mock.calls[0][1]).toEqual({
      walls: [{ start: { x: 0, y: 0 }, end: { x: 1000, y: 0 }, baseLevel: "L1", height: 3000, typeName: "300" }],
    });
  });

  it("reports invalid file items without sending", async () => {
    const tool = setup()("create_walls");
    const file = path.join(dir, "bad.json");
    await writeFile(file, JSON.stringify([{ start: { x: 0, y: 0 }, end: { x: 1, y: 0 }, baseLevel: "L1" }]), "utf8");
    const result = await tool.invoke({ dataFile: file });
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toMatch(/walls\[0\].*topLevel/);
    expect(sendCommand).not.toHaveBeenCalled();
  });
});

describe("create_family_type data files", () => {
  beforeEach(() => {
    resetConnectionMock();
    echoBatch();
  });

  it("reads CSV with parameters as JSON text or dotted headers", async () => {
    const tool = setup()("create_family_type");
    const csv = path.join(dir, "types.csv");
    await writeFile(
      csv,
      [
        "familyName,newName,parameters,parameters.Comments",
        'M_Concrete-Rectangular Beam,300x600,"{""b"":300,""h"":600}",',
        "M_Concrete-Rectangular Beam,B2,,typ",
      ].join("\n"),
      "utf8"
    );
    const result = await tool.invoke({ dataFile: csv });
    expect(result.isError).toBeUndefined();
    expect(sendCommand).toHaveBeenCalledWith("create_family_type", {
      types: [
        { familyName: "M_Concrete-Rectangular Beam", newName: "300x600", parameters: { b: 300, h: 600 } },
        { familyName: "M_Concrete-Rectangular Beam", newName: "B2", parameters: { Comments: "typ" } },
      ],
    });
  });

  it("still requires items or a data file", async () => {
    const tool = setup()("create_family_type");
    const result = await tool.invoke({});
    expect(result.isError).toBe(true);
    expect(sendCommand).not.toHaveBeenCalled();
  });
});

describe("summary option on existing bulk tools", () => {
  beforeEach(() => {
    resetConnectionMock();
    echoBatch();
  });

  it("create_beams returns the summary when asked", async () => {
    const tool = setup()("create_beams");
    const beam = { typeId: 5, start: { x: 0, y: 0 }, end: { x: 1000, y: 0 }, level: "L1" };
    const result = await tool.invoke({ beams: [beam, beam], summary: true });
    const body = JSON.parse(result.content[0].text);
    expect(body.Response).toMatchObject({ summary: true, createdIds: { count: 2, ranges: "1000-1001" } });
    expect(body.Response.results).toBeUndefined();
  });
});

describe("save_document", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ Success: true, Message: "Saved.", Response: { path: "C:/a.rvt", isModified: false } });
  });

  it("validates Save As arguments", () => {
    expect(saveDocumentSchema.safeParse({}).success).toBe(true);
    expect(saveDocumentSchema.safeParse({ compact: true }).success).toBe(true);
    expect(saveDocumentSchema.safeParse({ saveAsPath: "C:\\Models\\Tower.rvt", overwrite: true, asCentral: true }).success).toBe(true);
    expect(saveDocumentSchema.safeParse({ saveAsPath: "Tower.rvt" }).success).toBe(false);
    expect(saveDocumentSchema.safeParse({ saveAsPath: "C:/Tower.txt" }).success).toBe(false);
    expect(saveDocumentSchema.safeParse({ overwrite: true }).success).toBe(false);
  });

  it("forwards with a long timeout and rejects misuse before sending", async () => {
    const tool = setup()("save_document");
    await tool.invoke({ saveAsPath: "E:/out/Tower.rvt", overwrite: true });
    expect(sendCommand).toHaveBeenCalledWith("save_document", { saveAsPath: "E:/out/Tower.rvt", overwrite: true }, 600000);
    sendCommand.mockClear();
    const bad = await tool.invoke({ asCentral: true });
    expect(bad.isError).toBe(true);
    expect(sendCommand).not.toHaveBeenCalled();
  });
});

describe("open_document", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ Success: true, Message: "Opened.", Response: { title: "Tower" } });
  });

  it("validates open, detach and new-project arguments", () => {
    expect(openDocumentSchema.safeParse({ path: "E:/m/Tower.rvt" }).success).toBe(true);
    expect(openDocumentSchema.safeParse({ path: "E:/m/Central.rvt", detach: true, saveAsPath: "E:/m/Copy.rvt" }).success).toBe(true);
    expect(openDocumentSchema.safeParse({ saveAsPath: "E:/m/New.rvt" }).success).toBe(true);
    expect(openDocumentSchema.safeParse({ templatePath: "C:/t/Structural.rte", saveAsPath: "E:/m/New.rvt", overwrite: true }).success).toBe(true);
    expect(openDocumentSchema.safeParse({}).success).toBe(false);
    expect(openDocumentSchema.safeParse({ templatePath: "C:/t/Structural.rte" }).success).toBe(false);
    expect(openDocumentSchema.safeParse({ path: "E:/a.rvt", templatePath: "C:/t/S.rte" }).success).toBe(false);
    expect(openDocumentSchema.safeParse({ saveAsPath: "E:/m/New.rvt", detach: true }).success).toBe(false);
    expect(openDocumentSchema.safeParse({ path: "relative.rvt" }).success).toBe(false);
    expect(openDocumentSchema.safeParse({ templatePath: "C:/t/S.rvt", saveAsPath: "E:/m/New.rvt" }).success).toBe(false);
  });

  it("defaults activate to true and uses a long timeout", async () => {
    const tool = setup()("open_document");
    await tool.invoke({ path: "E:/m/Tower.rvt" });
    expect(sendCommand).toHaveBeenCalledWith("open_document", { path: "E:/m/Tower.rvt", activate: true }, 600000);
    await tool.invoke({ path: "E:/m/Tower.rvt", activate: false });
    expect(sendCommand).toHaveBeenLastCalledWith("open_document", { path: "E:/m/Tower.rvt", activate: false }, 600000);
  });
});
