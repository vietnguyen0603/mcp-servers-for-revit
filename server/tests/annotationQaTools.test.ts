import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerCreateRebarAnnotationTool } from "../src/tools/create_rebar_annotation.js";
import { registerDeleteOrphanedTagsTool } from "../src/tools/delete_orphaned_tags.js";
import { registerFindTagOverlapsTool } from "../src/tools/find_tag_overlaps.js";
import { resetConnectionMock, sendCommand, withRevitConnection } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, tools, getTool } = createFakeMcpServer();
  registerFindTagOverlapsTool(server);
  registerDeleteOrphanedTagsTool(server);
  registerCreateRebarAnnotationTool(server);
  return { tools, getTool };
}

describe("annotation QA and rebar tools", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ success: true, message: "ok", response: {} });
  });

  it("registers the three tools with the expected manifest safety flags", () => {
    const { tools } = setup();
    expect([...tools.keys()].sort()).toEqual(["create_rebar_annotation", "delete_orphaned_tags", "find_tag_overlaps"]);
    expect(TOOL_MANIFEST.find_tag_overlaps.readOnly).toBe(true);
    expect(TOOL_MANIFEST.delete_orphaned_tags).toMatchObject({ readOnly: false, destructive: true });
    expect(TOOL_MANIFEST.create_rebar_annotation.catalogs).toContain("annotate");
  });

  it("dispatches find_tag_overlaps with defaults applied", async () => {
    const { getTool } = setup();
    await getTool("find_tag_overlaps").invoke({ viewId: 12 });
    expect(sendCommand).toHaveBeenCalledWith("find_tag_overlaps", {
      viewId: 12,
      toleranceMm: 0,
      includeModelElements: false,
      maxPairs: 500,
    });
  });

  it("requires modelCategories when checking against model elements", async () => {
    const { getTool } = setup();
    const result = await getTool("find_tag_overlaps").invoke({ includeModelElements: true });
    expect(result.isError).toBe(true);
    expect(withRevitConnection).not.toHaveBeenCalled();
    expect(() => getTool("find_tag_overlaps").parse({ toleranceMm: -1 })).toThrow();
  });

  it("defaults delete_orphaned_tags to a dry run", async () => {
    const { getTool } = setup();
    await getTool("delete_orphaned_tags").invoke({});
    expect(sendCommand).toHaveBeenCalledWith("delete_orphaned_tags", { dryRun: true });

    await getTool("delete_orphaned_tags").invoke({ viewIds: [5], dryRun: false });
    expect(sendCommand).toHaveBeenLastCalledWith("delete_orphaned_tags", { viewIds: [5], dryRun: false });
    expect(() => getTool("delete_orphaned_tags").parse({ viewIds: [0] })).toThrow();
  });

  it("validates create_rebar_annotation mode and points", async () => {
    const { getTool } = setup();
    const tool = getTool("create_rebar_annotation");
    expect(tool.parse({})).toMatchObject({ mode: "Tag" });
    expect(() => tool.parse({ mode: "Dimension" })).toThrow();
    expect(() => tool.parse({ mode: "MultiRebar", dimensionLinePoint: { x: 1 } })).toThrow();

    await tool.invoke({ mode: "MultiRebar", rebarIds: [101, 102], dimensionLinePoint: { x: 0, y: 500 } });
    expect(sendCommand).toHaveBeenCalledWith("create_rebar_annotation", {
      mode: "MultiRebar",
      rebarIds: [101, 102],
      dimensionLinePoint: { x: 0, y: 500 },
    });
  });

  it("surfaces Revit-side failures as tool errors", async () => {
    const { getTool } = setup();
    sendCommand.mockResolvedValueOnce({ success: false, message: "No multi-rebar annotation types exist" });
    const result = await getTool("create_rebar_annotation").invoke({ mode: "MultiRebar" });
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toContain("multi-rebar");
  });
});
