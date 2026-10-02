import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerCreateDetailLinesTool } from "../src/tools/create_detail_lines.js";
import { registerCreateFilledRegionTool } from "../src/tools/create_filled_region.js";
import { registerCreateRevisionCloudTool } from "../src/tools/create_revision_cloud.js";
import { registerPlaceDetailComponentTool } from "../src/tools/place_detail_component.js";
import { resetConnectionMock, sendCommand, withRevitConnection } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

const TOOLS = ["create_detail_lines", "create_filled_region", "place_detail_component", "create_revision_cloud"];

function setup() {
  const { server, tools, getTool } = createFakeMcpServer();
  registerCreateDetailLinesTool(server);
  registerCreateFilledRegionTool(server);
  registerPlaceDetailComponentTool(server);
  registerCreateRevisionCloudTool(server);
  return { tools, getTool };
}

const p = (x: number, y: number) => ({ x, y });

describe("detailing tools", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ success: true, message: "ok", response: {} });
  });

  it("registers the detailing tools in the annotate catalog", () => {
    const { tools } = setup();
    expect([...tools.keys()].sort()).toEqual([...TOOLS].sort());
    for (const name of TOOLS) {
      expect(TOOL_MANIFEST[name].catalogs).toEqual(["annotate"]);
      expect(TOOL_MANIFEST[name].readOnly).toBe(false);
    }
  });

  it("accepts lines, arcs and polylines and applies arc defaults", async () => {
    const { getTool } = setup();
    await getTool("create_detail_lines").invoke({
      viewId: 12,
      lineStyle: "Thin Lines",
      lines: [
        { start: p(0, 0), end: p(1000, 0) },
        { center: p(0, 0), radius: 500 },
        { points: [p(0, 0), p(100, 0), p(100, 100)], closed: true, lineStyle: "Wide Lines" },
      ],
    });
    expect(sendCommand).toHaveBeenCalledWith("create_detail_lines", {
      viewId: 12,
      lineStyle: "Thin Lines",
      lines: [
        { start: p(0, 0), end: p(1000, 0) },
        { center: p(0, 0), radius: 500, startAngleDeg: 0, endAngleDeg: 360 },
        { points: [p(0, 0), p(100, 0), p(100, 100)], closed: true, lineStyle: "Wide Lines" },
      ],
    });
  });

  it("rejects malformed detail line items", () => {
    const tool = setup().getTool("create_detail_lines");
    expect(() => tool.parse({ lines: [] })).toThrow();
    expect(() => tool.parse({ lines: [{ start: p(0, 0) }] })).toThrow();
    expect(() => tool.parse({ lines: [{ center: p(0, 0), radius: -1 }] })).toThrow();
    expect(() => tool.parse({ lines: [{ center: p(0, 0), radius: 5, startAngleDeg: 90, endAngleDeg: 10 }] })).toThrow();
    expect(() => tool.parse({ lines: [{ points: [p(0, 0)] }] })).toThrow();
  });

  it("requires regions unless listing filled region types", async () => {
    const tool = setup().getTool("create_filled_region");
    const empty = await tool.invoke({});
    expect(empty.isError).toBe(true);
    expect(withRevitConnection).not.toHaveBeenCalled();

    await tool.invoke({ listTypes: true });
    expect(sendCommand).toHaveBeenLastCalledWith("create_filled_region", { listTypes: true });

    expect(() => tool.parse({ regions: [{ boundary: [p(0, 0), p(1, 0)] }] })).toThrow();
    expect(
      tool.parse({
        regions: [
          {
            boundary: [p(0, 0), p(1000, 0), p(1000, 1000), p(0, 1000)],
            holes: [[p(100, 100), p(200, 100), p(200, 200)]],
            masking: true,
          },
        ],
      })
    ).toBeTruthy();
  });

  it("requires a family identity and placement for detail components", () => {
    const tool = setup().getTool("place_detail_component");
    expect(() => tool.parse({ components: [{ location: p(0, 0) }] })).toThrow(/familyTypeId/);
    expect(() => tool.parse({ components: [{ familyTypeId: 5 }] })).toThrow(/location/);
    expect(() => tool.parse({ components: [{ familyTypeId: 5, start: p(0, 0) }] })).toThrow(/location/);
    expect(tool.parse({ components: [{ familyName: "Break Line", location: p(0, 0), rotationDegrees: 90 }] })).toBeTruthy();
    expect(tool.parse({ components: [{ familyTypeId: 7, start: p(0, 0), end: p(500, 0) }] })).toBeTruthy();
  });

  it("requires exactly one revision cloud shape", async () => {
    const tool = setup().getTool("create_revision_cloud");
    expect(() => tool.parse({ clouds: [{}] })).toThrow();
    expect(() =>
      tool.parse({ clouds: [{ rectangle: { min: p(0, 0), max: p(1, 1) }, points: [p(0, 0), p(1, 0), p(1, 1)] }] })
    ).toThrow();

    sendCommand.mockResolvedValueOnce({ success: false, message: "The project has no revisions." });
    const result = await tool.invoke({ clouds: [{ rectangle: { min: p(0, 0), max: p(1000, 500) } }] });
    expect(result.isError).toBe(true);
    expect(sendCommand).toHaveBeenCalledWith("create_revision_cloud", {
      clouds: [{ rectangle: { min: p(0, 0), max: p(1000, 500) } }],
    });
  });
});
