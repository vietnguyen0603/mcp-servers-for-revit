import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerCreateOffsetLevelsTool } from "../src/tools/create_offset_levels.js";
import { registerSetWallTopLevelsTool } from "../src/tools/set_wall_top_levels.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerCreateOffsetLevelsTool(server);
  registerSetWallTopLevelsTool(server);
  return getTool;
}

describe("top plate tools", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: { succeeded: 1, failed: 0 } });
  });

  it("forwards create_offset_levels with 1' TOP PLATE defaults", async () => {
    const tool = setup()("create_offset_levels");
    await tool.invoke({ levels: [{ name: "2ND" }, { levelId: 311, newName: "RF PLATE", offset: -200 }] });
    expect(sendCommand.mock.calls[0][0]).toBe("create_offset_levels");
    expect(sendCommand.mock.calls[0][1]).toEqual({
      levels: [{ name: "2ND" }, { levelId: 311, newName: "RF PLATE", offset: -200 }],
      offset: -304.8,
      suffix: " TOP PLATE",
      ifExists: "skip",
      matchExtents: true,
      isBuildingStory: false,
      planViews: "none",
    });
  });

  it("rejects offset levels without a source identifier or with a bad ifExists", () => {
    const tool = setup()("create_offset_levels");
    expect(() => tool.parse({ levels: [{ newName: "X" }] })).toThrow();
    expect(() => tool.parse({ levels: [] })).toThrow();
    expect(() => tool.parse({ levels: [{ name: "2ND" }], ifExists: "replace" })).toThrow();
  });

  it("forwards set_wall_top_levels by suffix with interior defaults", async () => {
    const tool = setup()("set_wall_top_levels");
    await tool.invoke({ suffix: " TOP PLATE", dryRun: true });
    expect(sendCommand.mock.calls[0][0]).toBe("set_wall_top_levels");
    expect(sendCommand.mock.calls[0][1]).toEqual({
      suffix: " TOP PLATE",
      wallFunction: "interior",
      topOffset: 0,
      tolerance: 25,
      dryRun: true,
    });
  });

  it("accepts level ids or names in mapping pairs", async () => {
    const tool = setup()("set_wall_top_levels");
    const mapping = [{ from: "3RD", to: "3RD TOP PLATE" }, { from: 694, to: 1201, topOffset: -50 }];
    await tool.invoke({ mapping, wallFunction: "all", elementIds: [634757] });
    expect(sendCommand.mock.calls[0][1]).toMatchObject({ mapping, wallFunction: "all", elementIds: [634757] });
  });

  it("requires mapping or suffix before calling Revit", async () => {
    const tool = setup()("set_wall_top_levels");
    const result = await tool.invoke({});
    expect(result.isError).toBe(true);
    expect(sendCommand).not.toHaveBeenCalled();
    expect(() => tool.parse({ mapping: [{ from: "3RD" }] })).toThrow();
  });

  it("places the tools in the expected catalogs", () => {
    expect(TOOL_MANIFEST.create_offset_levels.catalogs).toEqual(expect.arrayContaining(["structure", "architecture"]));
    expect(TOOL_MANIFEST.set_wall_top_levels.catalogs).toEqual(expect.arrayContaining(["modify", "structure"]));
    expect(TOOL_MANIFEST.set_wall_top_levels.readOnly).toBe(false);
  });
});
