import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { expandGridAxes, registerCreateGridsTool } from "../src/tools/create_grids.js";
import { registerModifyLevelsTool } from "../src/tools/modify_levels.js";
import { registerGetDocumentInfoTool } from "../src/tools/get_document_info.js";
import { registerCreateLevelTool } from "../src/tools/create_level.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerCreateGridsTool(server);
  registerModifyLevelsTool(server);
  registerGetDocumentInfoTool(server);
  registerCreateLevelTool(server);
  return getTool;
}

describe("datum tools", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: { succeeded: 1, failed: 0 } });
  });

  it("forwards explicit line and arc grids with options", async () => {
    const tool = setup()("create_grids");
    const grids = [
      { name: "C.5", start: { x: 0, y: 25000 }, end: { x: 148000, y: 25000 } },
      { name: "R1", center: { x: 0, y: 0 }, radius: 20000, startAngleDeg: 0, endAngleDeg: 90 },
    ];
    await tool.invoke({ grids, ifExists: "replace", gridTypeName: "6.5mm Bubble" });
    expect(sendCommand).toHaveBeenCalledWith("create_grids", { grids, ifExists: "replace", gridTypeName: "6.5mm Bubble" });
    expect(tool.description).toMatch(/millimetres/);
  });

  it("expands axes: direction x gives vertical grids at X positions", async () => {
    const tool = setup()("create_grids");
    await tool.invoke({
      axes: [
        { direction: "x", labels: ["1", "2", "5"], positions: [0, 10000, 42500], from: -5000, to: 98000 },
        { direction: "y", labels: ["A", "C.5"], positions: [0, 27500], from: -5000, to: 153000 },
      ],
    });
    expect(sendCommand).toHaveBeenCalledWith("create_grids", {
      grids: [
        { name: "1", start: { x: 0, y: -5000 }, end: { x: 0, y: 98000 } },
        { name: "2", start: { x: 10000, y: -5000 }, end: { x: 10000, y: 98000 } },
        { name: "5", start: { x: 42500, y: -5000 }, end: { x: 42500, y: 98000 } },
        { name: "A", start: { x: -5000, y: 0 }, end: { x: 153000, y: 0 } },
        { name: "C.5", start: { x: -5000, y: 27500 }, end: { x: 153000, y: 27500 } },
      ],
    });
  });

  it("appends expanded axes after explicit grids", () => {
    expect(expandGridAxes([{ direction: "y", labels: ["E.8"], positions: [61000], from: 0, to: 10 }])).toEqual([
      { name: "E.8", start: { x: 0, y: 61000 }, end: { x: 10, y: 61000 } },
    ]);
  });

  it("rejects invalid grid input", async () => {
    const tool = setup()("create_grids");
    expect(() => tool.parse({ axes: [{ direction: "x", labels: ["1", "2"], positions: [0], from: 0, to: 100 }] })).toThrow();
    expect(() => tool.parse({ grids: [{ name: "1", start: { x: 0, y: 0 }, end: { x: 0, y: 0 } }] })).toThrow();
    expect(() =>
      tool.parse({ grids: [{ name: "R", center: { x: 0, y: 0 }, radius: 10, startAngleDeg: 90, endAngleDeg: 10 }] })
    ).toThrow();
    expect(() => tool.parse({ grids: [], ifExists: "overwrite" })).toThrow();
    const result = await tool.invoke({});
    expect(result.isError).toBe(true);
    expect(sendCommand).not.toHaveBeenCalled();
  });

  it("forwards modify_levels and requires an identifier", async () => {
    const tool = setup()("modify_levels");
    const levels = [
      { name: "Level 1", newName: "MAI", elevation: 27000, isBuildingStory: true, structuralPlan: true },
      { levelId: 311, floorPlan: true },
    ];
    await tool.invoke({ levels });
    expect(sendCommand).toHaveBeenCalledWith("modify_levels", { levels });
    expect(() => tool.parse({ levels: [{ newName: "X" }] })).toThrow();
    expect(() => tool.parse({ levels: [] })).toThrow();
  });

  it("forwards get_document_info with no parameters", async () => {
    const tool = setup()("get_document_info");
    await tool.invoke({});
    expect(sendCommand).toHaveBeenCalledWith("get_document_info", {});
  });

  it("accepts planViews on create_level and keeps defaults when omitted", async () => {
    const tool = setup()("create_level");
    await tool.invoke({ data: [{ name: "HAM", elevation: -5500, planViews: "structural" }] });
    const sent = sendCommand.mock.calls[0][1] as { data: Array<Record<string, unknown>> };
    expect(sent.data[0].planViews).toBe("structural");
    expect(() => tool.parse({ data: [{ name: "X", elevation: 0, planViews: "ceiling" }] })).toThrow();
    const parsed = tool.parse({ data: [{ name: "X", elevation: 0 }] }) as { data: Array<Record<string, unknown>> };
    expect(parsed.data[0].planViews).toBeUndefined();
    expect(parsed.data[0].createFloorPlan).toBe(true);
    expect(parsed.data[0].createCeilingPlan).toBe(true);
  });

  it("places the tools in the expected catalogs", () => {
    expect(TOOL_MANIFEST.create_grids.catalogs).toEqual(expect.arrayContaining(["structure", "architecture"]));
    expect(TOOL_MANIFEST.modify_levels.catalogs).toEqual(expect.arrayContaining(["structure", "architecture"]));
    expect(TOOL_MANIFEST.get_document_info.catalogs).toContain("core");
    expect(TOOL_MANIFEST.get_document_info.readOnly).toBe(true);
  });
});
