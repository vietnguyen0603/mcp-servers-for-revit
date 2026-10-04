import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { registerCopyToLevelsTool } from "../src/tools/copy_to_levels.js";
import { registerDeleteElementsTool } from "../src/tools/delete_elements.js";
import { registerSetWorksetTool } from "../src/tools/set_workset.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerCopyToLevelsTool(server);
  registerDeleteElementsTool(server);
  registerSetWorksetTool(server);
  return getTool;
}

function ok() {
  sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: {} });
}

describe("copy_to_levels", () => {
  beforeEach(() => {
    resetConnectionMock();
    ok();
  });

  it("forwards a level range with defaults applied", async () => {
    const tool = setup()("copy_to_levels");
    const result = await tool.invoke({
      sourceLevel: "L5",
      targetLevels: { from: "L6", to: "L18" },
      categories: ["StructuralFraming", "OST_Floors"],
      commentsStartsWith: "sup",
    });
    expect(result.isError).toBeUndefined();
    expect(sendCommand).toHaveBeenCalledWith(
      "copy_to_levels",
      {
        sourceLevel: "L5",
        targetLevels: { from: "L6", to: "L18" },
        categories: ["StructuralFraming", "OST_Floors"],
        commentsStartsWith: "sup",
        skipExisting: true,
        returnIds: false,
      },
      1800000
    );
  });

  it("accepts arrays of names, ids and elevations", () => {
    const tool = setup()("copy_to_levels");
    expect(() =>
      tool.parse({ sourceLevel: 17000, targetLevels: ["L6", 123456, 20500.5], elementIds: [1, 2] })
    ).not.toThrow();
  });

  it("rejects bad input", () => {
    const tool = setup()("copy_to_levels");
    expect(() => tool.parse({ sourceLevel: "L5", targetLevels: [], categories: ["Walls"] })).toThrow();
    expect(() => tool.parse({ sourceLevel: "L5", targetLevels: { from: "L6" }, categories: ["Walls"] })).toThrow();
    expect(() =>
      tool.parse({ sourceLevel: "L5", targetLevels: { from: "L6", to: "L7", step: 2 }, categories: ["Walls"] })
    ).toThrow();
    expect(() => tool.parse({ sourceLevel: "L5", targetLevels: ["L6"], elementIds: [-1] })).toThrow();
    expect(() => tool.parse({ sourceLevel: "L5", targetLevels: ["L6"], categories: [] })).toThrow();
  });

  it("requires categories or elementIds", async () => {
    const tool = setup()("copy_to_levels");
    const result = await tool.invoke({ sourceLevel: "L5", targetLevels: ["L6"], commentsEquals: "sup20" });
    expect(result.isError).toBe(true);
    expect(sendCommand).not.toHaveBeenCalled();
  });
});

describe("delete_elements", () => {
  beforeEach(() => {
    resetConnectionMock();
    ok();
  });

  it("defaults to a dry run", async () => {
    const tool = setup()("delete_elements");
    await tool.invoke({ categories: ["StructuralFraming"], levels: ["L6", -3500], commentsEquals: "sup20" });
    expect(sendCommand).toHaveBeenCalledWith(
      "delete_elements",
      { categories: ["StructuralFraming"], levels: ["L6", -3500], commentsEquals: "sup20", dryRun: true },
      600000
    );
  });

  it("forwards dryRun false and type filters", () => {
    const tool = setup()("delete_elements");
    expect(tool.parse({ elementIds: [5], dryRun: false, typeNameEquals: "B300x600", markStartsWith: "B" })).toMatchObject({
      dryRun: false,
    });
  });

  it("refuses an unfiltered delete and bad levels", async () => {
    const tool = setup()("delete_elements");
    const result = await tool.invoke({ levels: ["L6"], dryRun: false });
    expect(result.isError).toBe(true);
    expect(sendCommand).not.toHaveBeenCalled();
    expect(() => tool.parse({ categories: ["Walls"], levels: [] })).toThrow();
  });
});

describe("set_workset", () => {
  beforeEach(() => {
    resetConnectionMock();
    ok();
  });

  it("lists worksets without a selection", async () => {
    const tool = setup()("set_workset");
    const result = await tool.invoke({ listWorksets: true });
    expect(result.isError).toBeUndefined();
    expect(sendCommand).toHaveBeenCalledWith(
      "set_workset",
      { listWorksets: true, createIfMissing: true, enableWorksharing: false },
      600000
    );
  });

  it("forwards a workset assignment", async () => {
    const tool = setup()("set_workset");
    await tool.invoke({ workset: "STR-Framing", categories: ["StructuralFraming"], levels: ["L5"] });
    expect(sendCommand).toHaveBeenCalledWith(
      "set_workset",
      {
        workset: "STR-Framing",
        categories: ["StructuralFraming"],
        levels: ["L5"],
        listWorksets: false,
        createIfMissing: true,
        enableWorksharing: false,
      },
      600000
    );
  });

  it("requires a workset name and a selection", async () => {
    const tool = setup()("set_workset");
    expect((await tool.invoke({ categories: ["Walls"] })).isError).toBe(true);
    expect((await tool.invoke({ workset: "A" })).isError).toBe(true);
    expect(sendCommand).not.toHaveBeenCalled();
    expect(() => tool.parse({ workset: "" })).toThrow();
  });
});
