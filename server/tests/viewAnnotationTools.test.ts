import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { registerGetViewAnnotationsTool } from "../src/tools/get_view_annotations.js";
import { registerModifyAnnotationsTool } from "../src/tools/modify_annotations.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerGetViewAnnotationsTool(server);
  registerModifyAnnotationsTool(server);
  return getTool;
}

describe("view annotation tools", () => {
  beforeEach(() => {
    resetConnectionMock();
  });

  it("dispatches get_view_annotations with paging defaults", async () => {
    const tool = setup()("get_view_annotations");
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: { total: 0 } });

    const result = await tool.invoke({ viewId: 5, kinds: ["textNotes", "dimensions"] });
    expect(sendCommand).toHaveBeenCalledWith("get_view_annotations", {
      viewId: 5,
      kinds: ["textNotes", "dimensions"],
      limit: 500,
      offset: 0,
    });
    expect(result.isError).toBeUndefined();
    expect(() => tool.parse({ kinds: ["walls"] })).toThrow();
    expect(() => tool.parse({ limit: 6000 })).toThrow();
  });

  it("validates modify_annotations operations per action", () => {
    const tool = setup()("modify_annotations");
    const ok = {
      operations: [
        { action: "move", elementIds: [1, 2], delta: { x: 100, y: 0 } },
        { action: "rotate", elementIds: [1], center: { x: 0, y: 0 }, angleDeg: 90 },
        { action: "setText", elementIds: [3], text: "Ø10-150-S2" },
        { action: "setLineStyle", elementIds: [4], lineStyle: "<Thin Lines>" },
        { action: "setLine", elementIds: [4], start: { x: 0, y: 0 }, end: { x: 10, y: 0 } },
        { action: "setType", elementIds: [5], typeName: "2.5mm Arial" },
        { action: "setParameters", elementIds: [5], parameters: { Comments: "checked" } },
        { action: "delete", elementIds: [6] },
      ],
    };
    expect(tool.parse(ok)).toBeTruthy();

    expect(() => tool.parse({ operations: [{ action: "explode", elementIds: [1] }] })).toThrow();
    expect(() => tool.parse({ operations: [{ action: "move", elementIds: [1] }] })).toThrow();
    expect(() => tool.parse({ operations: [{ action: "setText", elementIds: [], text: "x" }] })).toThrow();
    expect(() =>
      tool.parse({ operations: [{ action: "setLine", elementIds: [1, 2], start: { x: 0, y: 0 }, end: { x: 1, y: 0 } }] })
    ).toThrow();
    expect(() => tool.parse({ operations: [{ action: "setType", elementIds: [1] }] })).toThrow(/typeId or typeName/);
    expect(() =>
      tool.parse({ operations: [{ action: "delete", elementIds: [1], text: "unexpected" }] })
    ).toThrow();
  });

  it("flags PascalCase Revit failures as MCP errors", async () => {
    const tool = setup()("modify_annotations");
    sendCommand.mockResolvedValue({ Success: false, Message: "Elements not found: 9." });

    const result = await tool.invoke({ operations: [{ action: "delete", elementIds: [9] }] });
    expect(sendCommand).toHaveBeenCalledWith("modify_annotations", {
      operations: [{ action: "delete", elementIds: [9] }],
    });
    expect(result.isError).toBe(true);
  });
});
