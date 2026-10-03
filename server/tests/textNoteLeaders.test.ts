import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { registerCreateTextNoteTool } from "../src/tools/create_text_note.js";
import { registerModifyAnnotationsTool } from "../src/tools/modify_annotations.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerCreateTextNoteTool(server);
  registerModifyAnnotationsTool(server);
  return getTool;
}

describe("text note leaders and formatting", () => {
  beforeEach(() => {
    resetConnectionMock();
  });

  it("forwards leaders, attachments and format to create_text_note", async () => {
    const tool = setup()("create_text_note");
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: { succeeded: 1 } });

    const note = {
      text: "#5 @ 12\" OC\rEACH WAY",
      location: { x: 100, y: 200 },
      viewId: 7,
      leaders: [
        { end: { x: 50, y: 150 } },
        { end: { x: 300, y: 120, z: 0 }, elbow: { x: 260, y: 196 }, side: "Right", shape: "Straight" },
        { end: { x: 10, y: 10 }, shape: "Arc", side: "Left" },
      ],
      leaderLeftAttachment: "TopLine",
      leaderRightAttachment: "Midpoint",
      format: { allCaps: true, bold: false, list: "ArabicNumbers", headingLines: 1 },
    };
    const result = await tool.invoke({ notes: [note] });

    expect(sendCommand).toHaveBeenCalledWith("create_text_note", { notes: [note] });
    expect(result.isError).toBeUndefined();
  });

  it("validates create_text_note leader and format shapes", () => {
    const tool = setup()("create_text_note");
    const base = { text: "NOTES:", location: { x: 0, y: 0 } };

    expect(tool.parse({ notes: [base] })).toBeTruthy();
    expect(() => tool.parse({ notes: [{ ...base, leaders: [] }] })).toThrow();
    expect(() => tool.parse({ notes: [{ ...base, leaders: [{ elbow: { x: 0, y: 0 } }] }] })).toThrow();
    expect(() => tool.parse({ notes: [{ ...base, leaders: [{ end: { x: 0, y: 0 }, side: "Top" }] }] })).toThrow();
    expect(() => tool.parse({ notes: [{ ...base, leaders: [{ end: { x: 0, y: 0 }, shape: "Spline" }] }] })).toThrow();
    expect(() => tool.parse({ notes: [{ ...base, leaders: [{ end: { x: 0, y: 0 }, extra: 1 }] }] })).toThrow();
    expect(() => tool.parse({ notes: [{ ...base, leaderLeftAttachment: "Top" }] })).toThrow();
    expect(() => tool.parse({ notes: [{ ...base, format: { list: "Mixed" } }] })).toThrow();
    expect(() => tool.parse({ notes: [{ ...base, format: { headingLines: -1 } }] })).toThrow();
    expect(() => tool.parse({ notes: [{ ...base, format: { headingLines: 1.5 } }] })).toThrow();
    expect(() => tool.parse({ notes: [{ ...base, format: { strike: true } }] })).toThrow();
  });

  it("validates modify_annotations leader actions", async () => {
    const tool = setup()("modify_annotations");
    const ok = {
      operations: [
        { action: "addLeaders", elementIds: [11], leaders: [{ end: { x: 0, y: 0 } }] },
        {
          action: "setLeaders",
          elementIds: [11, 12],
          leaders: [{ end: { x: 5, y: 5 }, elbow: { x: 20, y: 30 }, side: "Left" }],
          leaderLeftAttachment: "BottomLine",
        },
        { action: "removeLeaders", elementIds: [13] },
      ],
    };
    expect(tool.parse(ok)).toBeTruthy();

    expect(() => tool.parse({ operations: [{ action: "addLeaders", elementIds: [1] }] })).toThrow();
    expect(() => tool.parse({ operations: [{ action: "setLeaders", elementIds: [1], leaders: [] }] })).toThrow();
    expect(() =>
      tool.parse({ operations: [{ action: "removeLeaders", elementIds: [1], leaders: [{ end: { x: 0, y: 0 } }] }] })
    ).toThrow();

    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: {} });
    await tool.invoke(ok);
    expect(sendCommand).toHaveBeenCalledWith("modify_annotations", ok);
  });
});
