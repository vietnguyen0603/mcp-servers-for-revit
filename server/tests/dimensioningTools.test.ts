import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerCreateGridDimensionsTool } from "../src/tools/create_grid_dimensions.js";
import { registerCreateSpotElevationsTool } from "../src/tools/create_spot_elevations.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, tools, getTool } = createFakeMcpServer();
  registerCreateGridDimensionsTool(server);
  registerCreateSpotElevationsTool(server);
  return { tools, getTool };
}

describe("dimensioning tools", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ success: true, message: "ok", response: {} });
  });

  it("registers both tools as model-changing annotate/structure tools", () => {
    const { tools } = setup();
    expect([...tools.keys()].sort()).toEqual(["create_grid_dimensions", "create_spot_elevations"]);
    for (const name of tools.keys()) {
      expect(TOOL_MANIFEST[name].catalogs).toEqual(["annotate", "structure"]);
      expect(TOOL_MANIFEST[name].readOnly).toBe(false);
    }
  });

  it("dispatches create_grid_dimensions with resolved defaults", async () => {
    const { getTool } = setup();
    await getTool("create_grid_dimensions").invoke({});
    expect(sendCommand).toHaveBeenCalledWith("create_grid_dimensions", {
      chain: true,
      overall: true,
      offsetMm: 1500,
      overallOffsetMm: 800,
      sides: ["Start"],
      skipExisting: true,
    });
  });

  it("validates grid dimension sides and ids", () => {
    const tool = setup().getTool("create_grid_dimensions");
    expect(() => tool.parse({ sides: ["Left"] })).toThrow();
    expect(() => tool.parse({ sides: [] })).toThrow();
    expect(() => tool.parse({ gridIds: [0] })).toThrow();
    expect(tool.parse({ sides: ["Start", "End"], gridIds: [12, 13], viewId: 5 })).toMatchObject({
      sides: ["Start", "End"],
      gridIds: [12, 13],
    });
  });

  it("dispatches create_spot_elevations with per-target defaults", async () => {
    const { getTool } = setup();
    await getTool("create_spot_elevations").invoke({
      targets: [{ elementId: 101, point: { x: 1000, y: 2000 } }],
    });
    expect(sendCommand).toHaveBeenCalledWith("create_spot_elevations", {
      kind: "Elevation",
      targets: [{ elementId: 101, point: { x: 1000, y: 2000 }, hasLeader: false }],
    });
  });

  it("validates spot elevation targets and kind", () => {
    const tool = setup().getTool("create_spot_elevations");
    expect(() => tool.parse({ targets: [] })).toThrow();
    expect(() => tool.parse({ targets: [{}] })).toThrow();
    expect(() => tool.parse({ kind: "Slope", targets: [{ elementId: 1 }] })).toThrow();
    expect(tool.parse({ kind: "Coordinate", targets: [{ elementId: 1 }] })).toMatchObject({ kind: "Coordinate" });
  });

  it("flags Revit-side failures as tool errors", async () => {
    const { getTool } = setup();
    sendCommand.mockResolvedValueOnce({ success: false, message: "View 'Sheet' must be a plan, section or elevation view." });
    const result = await getTool("create_grid_dimensions").invoke({ viewId: 9 });
    expect(result.isError).toBe(true);
  });
});
