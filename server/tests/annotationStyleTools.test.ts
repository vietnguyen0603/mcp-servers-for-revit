import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import {
  SETTING_KINDS,
  annotationSettingsSchema,
  registerManageAnnotationTypesTool,
  validateManageAnnotationTypes,
} from "../src/tools/manage_annotation_types.js";
import { registerSetGridDisplayTool } from "../src/tools/set_grid_display.js";
import { registerCreateGridsTool } from "../src/tools/create_grids.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerManageAnnotationTypesTool(server);
  registerSetGridDisplayTool(server);
  registerCreateGridsTool(server);
  return getTool;
}

describe("manage_annotation_types", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: { succeeded: 1, failed: 0 } });
  });

  it("forwards list with filters", async () => {
    const tool = setup()("manage_annotation_types");
    await tool.invoke({ action: "list", kind: "dimension", style: "linear", nameContains: "Diagonal" });
    expect(sendCommand).toHaveBeenCalledWith(
      "manage_annotation_types",
      { action: "list", kind: "dimension", style: "linear", nameContains: "Diagonal" },
      150000
    );
  });

  it("forwards a dimension type with units and tick mark", async () => {
    const tool = setup()("manage_annotation_types");
    const types = [
      {
        name: "2.5mm Arial - Diagonal",
        sourceName: "Linear - 3mm Arial",
        setAsDefault: true,
        settings: {
          textSize: 2.5,
          font: "Arial",
          tickMark: "Diagonal 3mm",
          textBackground: "transparent",
          witnessLineGap: 2,
          lineWeight: 1,
          color: "#000000",
          units: { unit: "mm", accuracy: 1, suppressTrailingZeros: true },
        },
      },
    ];
    const result = await tool.invoke({ action: "create", kind: "dimension", types });
    expect(result.isError).toBeUndefined();
    expect(sendCommand).toHaveBeenCalledWith("manage_annotation_types", { action: "create", kind: "dimension", types }, 150000);
  });

  it("accepts arrowhead, grid, level, viewport and spot settings", () => {
    const tool = setup()("manage_annotation_types");
    expect(() =>
      tool.parse({ action: "create", kind: "arrowhead", types: [{ name: "Diagonal 3mm", settings: { style: "Diagonal", tickSize: 3, heavyEndPen: 5 } }] })
    ).not.toThrow();
    expect(() =>
      tool.parse({
        action: "update",
        kind: "grid",
        types: [
          {
            name: "6.5mm Bubble",
            settings: { bubble: "M_Grid Head - Circle", bubbleEnd1: true, bubbleEnd2: false, centerSegment: "continuous", endSegmentWeight: 3, endSegmentColor: { r: 255, g: 0, b: 0 } },
          },
        ],
      })
    ).not.toThrow();
    expect(() =>
      tool.parse({ action: "update", kind: "spotElevation", types: [{ name: "Spot", settings: { units: { unit: "m", accuracy: 0.001, plusPrefix: true } } }] })
    ).not.toThrow();
    expect(() => tool.parse({ action: "update", kind: "viewport", types: [{ name: "Title w Line", settings: { showTitle: true, showExtensionLine: true } }] })).not.toThrow();
  });

  it("rejects bad values and unknown keys", () => {
    const tool = setup()("manage_annotation_types");
    expect(() => tool.parse({ action: "update", kind: "text", types: [{ name: "x", settings: { textSize: -1 } }] })).toThrow();
    expect(() => tool.parse({ action: "update", kind: "text", types: [{ name: "x", settings: { fontSize: 2 } }] })).toThrow();
    expect(() => tool.parse({ action: "update", kind: "text", types: [{ name: "x", settings: { color: "#12" } }] })).toThrow();
    expect(() => tool.parse({ action: "update", kind: "grid", types: [{ name: "x", settings: { endSegmentWeight: 17 } }] })).toThrow();
    expect(() => tool.parse({ action: "update", kind: "dimension", types: [{ name: "x", settings: { units: { unit: "furlong" } } }] })).toThrow();
    expect(() => tool.parse({ action: "remove", kind: "text" })).toThrow();
  });

  it("rejects settings that do not apply to the kind and missing kind/types", async () => {
    const tool = setup()("manage_annotation_types");
    let result = await tool.invoke({ action: "update", kind: "grid", types: [{ name: "x", settings: { textSize: 2.5 } }] });
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toMatch(/textSize does not apply to kind 'grid'/);
    result = await tool.invoke({ action: "create", types: [{ name: "x" }] });
    expect(result.isError).toBe(true);
    result = await tool.invoke({ action: "update", kind: "text" });
    expect(result.isError).toBe(true);
    result = await tool.invoke({ action: "setDefault", kind: "arrowhead", types: [{ name: "x" }] });
    expect(result.isError).toBe(true);
    expect(sendCommand).not.toHaveBeenCalled();
  });

  it("maps every settings key to at least one kind", () => {
    const keys = Object.keys(annotationSettingsSchema.shape);
    expect(keys.sort()).toEqual(Object.keys(SETTING_KINDS).sort());
    expect(validateManageAnnotationTypes({ action: "list" })).toBeNull();
  });
});

describe("set_grid_display", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: { views: [] } });
  });

  it("forwards bubbles, groups, extents and propagation", async () => {
    const tool = setup()("set_grid_display");
    const args = {
      views: [101, 102],
      bubbles: "top-left",
      groups: [{ grids: ["1", 345], bubbles: "bottom" }],
      extents: { offsetPaperMm: 12 },
      propagateToViews: [201, 202],
    };
    await tool.invoke(args);
    expect(sendCommand).toHaveBeenCalledWith("set_grid_display", args, 200000);
  });

  it("accepts clipToCrop and grid names", async () => {
    const tool = setup()("set_grid_display");
    await tool.invoke({ views: [5], grids: ["A", "C.5"], extents: "clipToCrop" });
    expect(sendCommand).toHaveBeenCalledWith("set_grid_display", { views: [5], grids: ["A", "C.5"], extents: "clipToCrop" }, 200000);
  });

  it("rejects invalid input and empty requests", async () => {
    const tool = setup()("set_grid_display");
    expect(() => tool.parse({ views: [], bubbles: "top" })).toThrow();
    expect(() => tool.parse({ views: [1], bubbles: "north" })).toThrow();
    expect(() => tool.parse({ views: [1], extents: { offsetMm: 1, offsetPaperMm: 2 } })).toThrow();
    expect(() => tool.parse({ views: [1], extents: {} })).toThrow();
    expect(() => tool.parse({ views: [1], extents: "fit" })).toThrow();
    const result = await tool.invoke({ views: [1] });
    expect(result.isError).toBe(true);
    expect(sendCommand).not.toHaveBeenCalled();
  });
});

describe("create_grids vertical extents", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: { succeeded: 1, failed: 0 } });
  });

  it("forwards verticalExtents and updateExtents", async () => {
    const tool = setup()("create_grids");
    const grids = [{ name: "1", start: { x: 0, y: 0 }, end: { x: 0, y: 10000 } }];
    await tool.invoke({ grids, verticalExtents: { bottom: -6000, top: 160000 }, updateExtents: true });
    expect(sendCommand).toHaveBeenCalledWith("create_grids", {
      grids,
      verticalExtents: { bottom: -6000, top: 160000 },
      updateExtents: true,
    });
    expect(tool.description).toMatch(/all levels/);
  });

  it("re-spans all existing grids when only updateExtents is given", async () => {
    const tool = setup()("create_grids");
    await tool.invoke({ updateExtents: true });
    expect(sendCommand).toHaveBeenCalledWith("create_grids", { updateExtents: true });
    sendCommand.mockClear();
    const result = await tool.invoke({ updateExtents: true, verticalExtents: "none" });
    expect(result.isError).toBe(true);
    expect(sendCommand).not.toHaveBeenCalled();
  });

  it("rejects an inverted range", () => {
    const tool = setup()("create_grids");
    expect(() => tool.parse({ updateExtents: true, verticalExtents: { bottom: 100, top: 0 } })).toThrow();
    expect(() => tool.parse({ updateExtents: true, verticalExtents: "someLevels" })).toThrow();
  });
});
