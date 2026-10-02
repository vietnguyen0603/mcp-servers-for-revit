import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerAlignViewportsTool } from "../src/tools/align_viewports.js";
import { registerCreateCalloutTool } from "../src/tools/create_callout.js";
import { registerSetCropRegionTool } from "../src/tools/set_crop_region.js";
import { registerSetViewRangeTool } from "../src/tools/set_view_range.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

const TOOLS = ["create_callout", "set_crop_region", "set_view_range", "align_viewports"];

function setup() {
  const { server, tools, getTool } = createFakeMcpServer();
  registerCreateCalloutTool(server);
  registerSetCropRegionTool(server);
  registerSetViewRangeTool(server);
  registerAlignViewportsTool(server);
  return { tools, getTool };
}

describe("view layout tools", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ success: true, message: "ok", response: {} });
  });

  it("registers the tools as model-changing views tools", () => {
    const { tools } = setup();
    expect([...tools.keys()].sort()).toEqual([...TOOLS].sort());
    for (const name of TOOLS) {
      expect(TOOL_MANIFEST[name].catalogs[0], name).toBe("views");
      expect(TOOL_MANIFEST[name].readOnly, name).toBe(false);
    }
  });

  it("validates and dispatches create_callout", async () => {
    const { getTool } = setup();
    const tool = getTool("create_callout");
    expect(() => tool.parse({ callouts: [] })).toThrow();
    expect(() => tool.parse({ callouts: [{ parentViewId: 5, min: { x: 0, y: 0 } }] })).toThrow();

    const args = { callouts: [{ parentViewId: 5, min: { x: 0, y: 0 }, max: { x: 3000, y: 2000 }, scale: 20 }] };
    await tool.invoke(args);
    expect(sendCommand).toHaveBeenCalledWith("create_callout", args);
  });

  it("rejects rectangle and fitToElementIds together in set_crop_region", async () => {
    const { getTool } = setup();
    const tool = getTool("set_crop_region");
    expect(() =>
      tool.parse({
        views: [{ viewId: 1, rectangle: { min: { x: 0, y: 0 }, max: { x: 1, y: 1 } }, fitToElementIds: [2] }],
      })
    ).toThrow(/either rectangle or fitToElementIds/);
    expect(() => tool.parse({ views: [{ viewId: 1, annotationOffsetsMm: { left: -1 } }] })).toThrow();

    await tool.invoke({ views: [{ viewId: 1, fitToElementIds: [2, 3], marginMm: 300, cropActive: true }] });
    expect(sendCommand).toHaveBeenCalledWith("set_crop_region", {
      views: [{ viewId: 1, fitToElementIds: [2, 3], marginMm: 300, cropActive: true }],
    });
  });

  it("accepts level ids or special level names in set_view_range", () => {
    const { getTool } = setup();
    const tool = getTool("set_view_range");
    expect(
      tool.parse({ views: [{ viewId: 7, cutPlaneMm: 1200, topLevelId: "LevelAbove", viewDepthLevelId: 99 }] })
    ).toBeTruthy();
    expect(() => tool.parse({ views: [{ viewId: 7, topLevelId: "Roof" }] })).toThrow();
  });

  it("defaults align_viewports to ViewOrigin", async () => {
    const { getTool } = setup();
    const tool = getTool("align_viewports");
    expect(() => tool.parse({ referenceViewportId: 1, viewportIds: [] })).toThrow();

    await tool.invoke({ referenceViewportId: 1, viewportIds: [2, 3] });
    expect(sendCommand).toHaveBeenCalledWith("align_viewports", {
      referenceViewportId: 1,
      viewportIds: [2, 3],
      align: "ViewOrigin",
    });
  });
});
