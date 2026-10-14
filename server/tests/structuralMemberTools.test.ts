import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerAttachColumnsTool } from "../src/tools/attach_columns.js";
import { registerModifyStructuralMembersTool } from "../src/tools/modify_structural_members.js";
import { registerSummarizeStructuralMembersTool } from "../src/tools/summarize_structural_members.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerModifyStructuralMembersTool(server);
  registerSummarizeStructuralMembersTool(server);
  registerAttachColumnsTool(server);
  return getTool;
}

describe("structural member tools", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: { succeeded: 1, failed: 0 } });
  });

  it("forwards modify_structural_members with filter, changes and defaults", async () => {
    const tool = setup()("modify_structural_members");
    await tool.invoke({
      filter: { category: "framing", comments: ["TG-1", "GH-1"] },
      set: { structuralUsage: "girder" },
    });
    expect(sendCommand.mock.calls[0][0]).toBe("modify_structural_members");
    expect(sendCommand.mock.calls[0][1]).toEqual({
      filter: { category: "framing", comments: ["TG-1", "GH-1"] },
      set: { structuralUsage: "girder" },
      dryRun: false,
      maxElements: 5000,
    });
  });

  it("accepts a type swap with column top changes", () => {
    const tool = setup()("modify_structural_members");
    expect(() =>
      tool.parse({
        filter: { elementIds: [709224, 709939] },
        set: { familyName: "Timber-Column", typeName: "6x6", topLevel: "2ND", topOffset: 27.4 },
      })
    ).not.toThrow();
  });

  it("rejects empty changes, typeName without familyName and unknown usage", () => {
    const tool = setup()("modify_structural_members");
    expect(() => tool.parse({ filter: { category: "all" }, set: {} })).toThrow();
    expect(() => tool.parse({ filter: { category: "all" }, set: { typeName: "6x6" } })).toThrow();
    expect(() => tool.parse({ filter: { category: "all" }, set: { structuralUsage: "header" } })).toThrow();
  });

  it("refuses to modify without any filter criterion", async () => {
    const tool = setup()("modify_structural_members");
    const result = await tool.invoke({ set: { structuralUsage: "girder" } });
    expect(result.isError).toBe(true);
    expect(sendCommand).not.toHaveBeenCalled();
  });

  it("forwards summarize_structural_members with comments + type grouping by default", async () => {
    const tool = setup()("summarize_structural_members");
    await tool.invoke({});
    expect(sendCommand.mock.calls[0][0]).toBe("summarize_structural_members");
    expect(sendCommand.mock.calls[0][1]).toEqual({ filter: {}, groupBy: ["comments", "type"], maxIds: 50 });
  });

  it("rejects an unknown groupBy key", () => {
    const tool = setup()("summarize_structural_members");
    expect(() => tool.parse({ groupBy: ["size"] })).toThrow();
  });

  it("lists both tools in the manifest, summarize as read-only", () => {
    expect(TOOL_MANIFEST.modify_structural_members.readOnly).toBe(false);
    expect(TOOL_MANIFEST.summarize_structural_members.readOnly).toBe(true);
  });
  it("forwards attach_columns with auto targets and defaults", async () => {
    const tool = setup()("attach_columns");
    await tool.invoke({ filter: { category: "columns", level: "L1" }, attachTop: {} });
    expect(sendCommand.mock.calls[0][0]).toBe("attach_columns");
    expect(sendCommand.mock.calls[0][1]).toEqual({
      filter: { category: "columns", level: "L1" },
      attachTop: { targets: "auto", justification: "minimum", cutStyle: "cutColumn", offsetMm: 0, searchRangeMm: 5000 },
      detachTop: false,
      detachBase: false,
      dryRun: false,
      maxElements: 2000,
    });
  });

  it("accepts explicit targets and detaching the other end", () => {
    const tool = setup()("attach_columns");
    expect(() =>
      tool.parse({
        filter: { elementIds: [1001] },
        attachBase: { targets: [2002, 2003], justification: "maximum", cutStyle: "none", offsetMm: -50, categories: ["framing"] },
        detachTop: true,
      })
    ).not.toThrow();
    expect(() => tool.parse({ filter: {}, attachTop: { targets: "nearest" } })).toThrow();
    expect(() => tool.parse({ filter: {}, attachTop: { justification: "centre" } })).toThrow();
    expect(() => tool.parse({ filter: {}, attachTop: { categories: ["walls"] } })).toThrow();
  });

  it("refuses attach_columns without an action, with attach+detach on one end, or without a filter", async () => {
    const tool = setup()("attach_columns");
    expect((await tool.invoke({ filter: { category: "columns" } })).isError).toBe(true);
    expect((await tool.invoke({ filter: { category: "columns" }, attachTop: {}, detachTop: true })).isError).toBe(true);
    expect((await tool.invoke({ attachTop: {} })).isError).toBe(true);
    expect(sendCommand).not.toHaveBeenCalled();
    expect(TOOL_MANIFEST.attach_columns.readOnly).toBe(false);
  });
});
