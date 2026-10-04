import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerCreateDimensionsTool } from "../src/tools/create_dimensions.js";
import { registerModifyAnnotationsTool } from "../src/tools/modify_annotations.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerCreateDimensionsTool(server);
  registerModifyAnnotationsTool(server);
  return getTool;
}

const p = (x: number, y: number) => ({ x, y });

describe("create_dimensions", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: { succeeded: 1 } });
  });

  it("is a model-changing annotate tool that mentions detail lines and type ids", () => {
    const tool = setup()("create_dimensions");
    expect(TOOL_MANIFEST.create_dimensions.readOnly).toBe(false);
    expect(tool.description).toMatch(/detail lines/);
    expect(tool.description).toMatch(/dimensionStyleId/);
  });

  it("dispatches an explicit reference chain with text and per-segment overrides", async () => {
    const tool = setup()("create_dimensions");
    const dimension = {
      references: [
        { elementId: 11 },
        { elementId: 12, end: "start" },
        { elementId: 13, end: "end" },
        { elementId: 14 },
      ],
      linePoint: p(0, -100),
      dimensionStyleId: 555,
      text: { suffix: "TYP" },
      segments: [{ index: 1, override: "EQ" }, { index: 2, below: "CLR" }],
    };
    const result = await tool.invoke({ dimensions: [dimension] });

    expect(result.isError).toBeUndefined();
    expect(sendCommand).toHaveBeenCalledWith("create_dimensions", {
      dimensions: [
        {
          ...dimension,
          references: [
            { elementId: 11, end: "curve" },
            { elementId: 12, end: "start" },
            { elementId: 13, end: "end" },
            { elementId: 14, end: "curve" },
          ],
        },
      ],
    });
  });

  it("keeps legacy point and elementIds dimensions valid", () => {
    const tool = setup()("create_dimensions");
    expect(
      tool.parse({
        dimensions: [
          { startPoint: { x: 0, y: 0, z: 0 }, endPoint: { x: 1000, y: 0, z: 0 }, dimensionType: "Linear", viewId: -1, dimensionStyleId: -1 },
          { startPoint: p(0, 0), endPoint: p(1000, 0), elementIds: [1, 2], linePoint: p(500, 300) },
          { startPoint: p(0, 0), endPoint: p(1000, 0), elementIds: [], text: { override: "SEE PLAN" } },
        ],
      })
    ).toBeTruthy();
  });

  it("rejects incomplete or conflicting dimension definitions", () => {
    const tool = setup()("create_dimensions");
    const bad = [
      {},
      { startPoint: p(0, 0) },
      { references: [{ elementId: 1 }, { elementId: 2 }] },
      { references: [{ elementId: 1 }], linePoint: p(0, 0) },
      { references: [{ elementId: 1 }, { elementId: 2 }], elementIds: [3, 4], linePoint: p(0, 0) },
      { references: [{ elementId: 1, end: "middle" }, { elementId: 2 }], linePoint: p(0, 0) },
      { startPoint: p(0, 0), endPoint: p(1, 0), elementIds: [1] },
      { startPoint: p(0, 0), endPoint: p(1, 0), text: { value: "EQ" } },
      { startPoint: p(0, 0), endPoint: p(1, 0), segments: [{ index: -1, override: "EQ" }] },
    ];
    for (const dimension of bad) {
      expect(() => tool.parse({ dimensions: [dimension] }), JSON.stringify(dimension)).toThrow();
    }
    expect(() => tool.parse({ dimensions: [] })).toThrow();
  });

  it("flags a Revit-side failure (e.g. no reference found) as an MCP error", async () => {
    const tool = setup()("create_dimensions");
    sendCommand.mockResolvedValueOnce({ Success: false, Message: "Created 0 of 1 dimensions." });
    const result = await tool.invoke({ dimensions: [{ startPoint: p(0, 0), endPoint: p(76.2, 0) }] });
    expect(result.isError).toBe(true);
  });
});

describe("modify_annotations setDimensionText", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: {} });
  });

  it("dispatches text and segment edits", async () => {
    const tool = setup()("modify_annotations");
    const operations = [
      { action: "setDimensionText", elementIds: [7], text: { override: "D/3", prefix: "", below: "TYP" } },
      { action: "setDimensionText", elementIds: [8], segments: [{ index: 0, override: "\"b\"" }] },
    ];
    await tool.invoke({ operations });
    expect(sendCommand).toHaveBeenCalledWith("modify_annotations", { operations });
  });

  it("requires text or segments and rejects unknown text fields", () => {
    const tool = setup()("modify_annotations");
    expect(() => tool.parse({ operations: [{ action: "setDimensionText", elementIds: [7] }] })).toThrow(
      /text or segments/
    );
    expect(() =>
      tool.parse({ operations: [{ action: "setDimensionText", elementIds: [7], text: { color: "red" } }] })
    ).toThrow();
  });
});
