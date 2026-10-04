import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerArrayAnnotationsTool } from "../src/tools/array_annotations.js";
import { registerCreateDetailLinesTool } from "../src/tools/create_detail_lines.js";
import { registerListDetailGroupsTool } from "../src/tools/list_detail_groups.js";
import { registerModifyAnnotationsTool } from "../src/tools/modify_annotations.js";
import { registerModifyDetailGroupsTool } from "../src/tools/modify_detail_groups.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerArrayAnnotationsTool(server);
  registerCreateDetailLinesTool(server);
  registerListDetailGroupsTool(server);
  registerModifyAnnotationsTool(server);
  registerModifyDetailGroupsTool(server);
  return getTool;
}

const p = (x: number, y: number) => ({ x, y });

describe("drafting productivity tools", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: {} });
  });

  it("places the new tools in the annotate catalog", () => {
    for (const name of ["array_annotations", "list_detail_groups", "modify_detail_groups"]) {
      expect(TOOL_MANIFEST[name].catalogs).toEqual(["annotate"]);
    }
    expect(TOOL_MANIFEST.list_detail_groups.readOnly).toBe(true);
    expect(TOOL_MANIFEST.array_annotations.readOnly).toBe(false);
    expect(TOOL_MANIFEST.modify_detail_groups.readOnly).toBe(false);
  });

  it("accepts polyline fillet radii and checks their count", async () => {
    const tool = setup()("create_detail_lines");
    const stirrup = [p(0, 0), p(300, 0), p(300, 500), p(0, 500)];
    await tool.invoke({ lines: [{ points: stirrup, closed: true, filletRadius: 25 }] });
    expect(sendCommand).toHaveBeenCalledWith("create_detail_lines", {
      lines: [{ points: stirrup, closed: true, filletRadius: 25 }],
    });

    expect(tool.parse({ lines: [{ points: stirrup, filletRadii: [0, 25, 25, 0] }] })).toBeTruthy();
    expect(() => tool.parse({ lines: [{ points: stirrup, filletRadii: [25, 25] }] })).toThrow(/one value per point/);
    expect(() => tool.parse({ lines: [{ points: stirrup, filletRadius: -1 }] })).toThrow();
    expect(() => tool.parse({ lines: [{ start: p(0, 0), end: p(1, 0), filletRadius: 5 }] })).toThrow();
  });

  it("validates mirror and flip operations", () => {
    const tool = setup()("modify_annotations");
    const parsed = tool.parse({
      operations: [
        { action: "mirror", elementIds: [1, 2], axis: { start: p(0, 0), end: p(0, 100) } },
        { action: "mirror", elementIds: [3], axis: { start: p(0, 0), end: p(100, 0) }, copy: true },
        { action: "flip", elementIds: [4, 5] },
      ],
    }) as { operations: Array<{ copy?: boolean }> };
    expect(parsed.operations[0].copy).toBe(false);
    expect(parsed.operations[1].copy).toBe(true);

    expect(() => tool.parse({ operations: [{ action: "mirror", elementIds: [1] }] })).toThrow();
    expect(() =>
      tool.parse({ operations: [{ action: "mirror", elementIds: [1], axis: { start: p(0, 0) } }] })
    ).toThrow();
    expect(() => tool.parse({ operations: [{ action: "flip", elementIds: [] }] })).toThrow();
    expect(() => tool.parse({ operations: [{ action: "flip", elementIds: [1], copy: true }] })).toThrow();
  });

  it("validates linear and point arrays", async () => {
    const tool = setup()("array_annotations");
    const along = { start: p(0, 0), end: p(1000, 0) };
    await tool.invoke({
      arrays: [
        { elementIds: [7], mode: "linear", along, spacing: 152.4 },
        { elementIds: [7], mode: "linear", direction: p(1, 0), spacing: 150, count: 5 },
        { elementIds: [7, 8], mode: "points", points: [p(0, 0), p(50, 50)] },
      ],
    });
    expect(sendCommand).toHaveBeenCalledWith("array_annotations", {
      arrays: [
        { elementIds: [7], mode: "linear", along, spacing: 152.4, includeOriginal: true },
        { elementIds: [7], mode: "linear", direction: p(1, 0), spacing: 150, count: 5, includeOriginal: true },
        { elementIds: [7, 8], mode: "points", points: [p(0, 0), p(50, 50)] },
      ],
    });

    expect(tool.parse({ arrays: [{ elementIds: [1], mode: "linear", along, count: 4 }] })).toBeTruthy();
    expect(() => tool.parse({ arrays: [{ elementIds: [1], mode: "linear", spacing: 100, count: 3 }] })).toThrow(
      /along or direction/
    );
    expect(() =>
      tool.parse({ arrays: [{ elementIds: [1], mode: "linear", direction: p(1, 0), spacing: 100 }] })
    ).toThrow(/needs spacing and count/);
    expect(() =>
      tool.parse({ arrays: [{ elementIds: [1], mode: "linear", direction: p(1, 0), count: 3, fit: "fill" }] })
    ).toThrow(/fit 'fill'/);
    expect(() => tool.parse({ arrays: [{ elementIds: [1], mode: "linear", along, spacing: 0 }] })).toThrow();
    expect(() => tool.parse({ arrays: [{ elementIds: [1], mode: "points", points: [] }] })).toThrow();
    expect(() => tool.parse({ arrays: [{ elementIds: [1], mode: "radial", points: [p(0, 0)] }] })).toThrow();
  });

  it("lists detail groups with paging defaults", async () => {
    const tool = setup()("list_detail_groups");
    await tool.invoke({ nameContains: "SCREW" });
    expect(sendCommand).toHaveBeenCalledWith("list_detail_groups", { nameContains: "SCREW", limit: 500, offset: 0 });
    expect(() => tool.parse({ limit: 0 })).toThrow();
  });

  it("validates detail group operations", async () => {
    const tool = setup()("modify_detail_groups");
    const ok = {
      viewId: 10,
      operations: [
        { action: "place", groupTypeName: "TT-SCREW", location: p(100, 200) },
        { action: "place", groupTypeId: 55, viewId: 11, location: p(0, 0), sourceInstanceId: 56 },
        { action: "create", elementIds: [1, 2, 3], name: "TT-NAIL" },
        { action: "ungroup", elementIds: [57] },
      ],
    };
    expect(tool.parse(ok)).toBeTruthy();
    await tool.invoke(ok);
    expect(sendCommand).toHaveBeenCalledWith("modify_detail_groups", ok);

    expect(() => tool.parse({ operations: [{ action: "place", location: p(0, 0) }] })).toThrow(/groupTypeId or groupTypeName/);
    expect(() => tool.parse({ operations: [{ action: "place", groupTypeId: 5 }] })).toThrow();
    expect(() => tool.parse({ operations: [{ action: "create", elementIds: [] }] })).toThrow();
    expect(() => tool.parse({ operations: [{ action: "explode", elementIds: [1] }] })).toThrow();
  });
});
