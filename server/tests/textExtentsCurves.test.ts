import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () => await import("./helpers/connectionMock.js"));

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerCreateTextNoteTool } from "../src/tools/create_text_note.js";
import { registerMeasureTextNotesTool } from "../src/tools/measure_text_notes.js";
import { registerModifyAnnotationsTool } from "../src/tools/modify_annotations.js";
import { registerPlaceDetailComponentTool } from "../src/tools/place_detail_component.js";
import { registerCreateDetailLinesTool } from "../src/tools/create_detail_lines.js";
import { registerDrawDetailTool } from "../src/tools/draw_detail.js";
import { createDetailConverter } from "../src/utils/detailUnits.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerCreateTextNoteTool(server);
  registerMeasureTextNotesTool(server);
  registerModifyAnnotationsTool(server);
  registerPlaceDetailComponentTool(server);
  registerCreateDetailLinesTool(server);
  registerDrawDetailTool(server);
  return getTool;
}

const ok = { Success: true, Message: "ok", Response: {} };

describe("measure_text_notes", () => {
  beforeEach(() => resetConnectionMock());

  it("is a read-only annotate tool", () => {
    expect(TOOL_MANIFEST.measure_text_notes).toMatchObject({ catalogs: ["annotate"], readOnly: true });
  });

  it("forwards ids or a view and validates the inputs", async () => {
    const tool = setup()("measure_text_notes");
    sendCommand.mockResolvedValue(ok);
    await tool.invoke({ elementIds: [11, 12] });
    expect(sendCommand).toHaveBeenCalledWith("measure_text_notes", { elementIds: [11, 12] });
    await tool.invoke({ viewId: 5, limit: 20 });
    expect(sendCommand).toHaveBeenCalledWith("measure_text_notes", { viewId: 5, limit: 20 });

    expect(tool.parse({})).toBeTruthy();
    expect(() => tool.parse({ elementIds: [] })).toThrow();
    expect(() => tool.parse({ limit: 0 })).toThrow();
    expect(() => tool.parse({ limit: 5000 })).toThrow();
  });
});

describe("create_text_note extents and imperial input", () => {
  beforeEach(() => resetConnectionMock());

  it("converts unit strings to mm and forwards fitWidth/measure", async () => {
    const tool = setup()("create_text_note");
    sendCommand.mockResolvedValue(ok);
    await tool.invoke({
      notes: [{ text: "TYP.", location: { x: "2'-9\"", y: "3/4\"" }, width: '1 1/2"', fitWidth: true, measure: true }],
    });
    const sent = sendCommand.mock.calls[0][1] as any;
    expect(sent.notes[0].location).toEqual({ x: 838.2, y: 19.05 });
    expect(sent.notes[0].width).toBe(38.1);
    expect(sent.notes[0]).toMatchObject({ fitWidth: true, measure: true });
  });

  it("keeps numbers unchanged and rejects bad lengths", () => {
    const tool = setup()("create_text_note");
    const parsed = tool.parse({ notes: [{ text: "A", location: { x: 100.5, y: -20 }, width: 40 }] }) as any;
    expect(parsed.notes[0].location).toEqual({ x: 100.5, y: -20 });
    expect(parsed.notes[0].width).toBe(40);
    expect(() => tool.parse({ notes: [{ text: "A", location: { x: "abc", y: 0 } }] })).toThrow();
    expect(() => tool.parse({ notes: [{ text: "A", location: { x: 0, y: 0 }, width: "-1in" }] })).toThrow();
    expect(() => tool.parse({ notes: [{ text: "A", location: { x: 0, y: 0 }, fitWidth: "yes" }] })).toThrow();
  });
});

describe("modify_annotations setCurve and curve copies", () => {
  beforeEach(() => resetConnectionMock());

  it("accepts setCurve with an optional arc mid and unit strings", async () => {
    const tool = setup()("modify_annotations");
    sendCommand.mockResolvedValue(ok);
    await tool.invoke({
      operations: [
        { action: "setCurve", elementIds: [9], start: { x: 0, y: 0 }, end: { x: "1'", y: 0 } },
        { action: "setCurve", elementIds: [10], start: { x: 0, y: 0 }, end: { x: 100, y: 0 }, mid: { x: 50, y: 20 } },
      ],
    });
    const sent = sendCommand.mock.calls[0][1] as any;
    expect(sent.operations[0].end).toEqual({ x: 304.8, y: 0 });
    expect(sent.operations[1].mid).toEqual({ x: 50, y: 20 });
    expect(() => tool.parse({ operations: [{ action: "setCurve", elementIds: [9], start: { x: 0, y: 0 } }] })).toThrow();
  });

  it("requires exactly one of delta, curve or copyTo, and one element for curve copies", () => {
    const tool = setup()("modify_annotations");
    const line = { start: { x: 0, y: 0 }, end: { x: 500, y: 0 } };
    expect(tool.parse({ operations: [{ action: "copy", elementIds: [1, 2], delta: { x: 10, y: 0 } }] })).toBeTruthy();
    expect(tool.parse({ operations: [{ action: "copy", elementIds: [1], curve: line }] })).toBeTruthy();
    expect(tool.parse({ operations: [{ action: "copy", elementIds: [1], copyTo: [line, line] }] })).toBeTruthy();

    expect(() => tool.parse({ operations: [{ action: "copy", elementIds: [1] }] })).toThrow();
    expect(() => tool.parse({ operations: [{ action: "copy", elementIds: [1], delta: { x: 1, y: 0 }, curve: line }] })).toThrow();
    expect(() => tool.parse({ operations: [{ action: "copy", elementIds: [1, 2], curve: line }] })).toThrow();
    expect(() => tool.parse({ operations: [{ action: "copy", elementIds: [1], copyTo: [] }] })).toThrow();
    expect(() => tool.parse({ operations: [{ action: "copy", elementIds: [1], curve: { ...line, extra: 1 } }] })).toThrow();
  });
});

describe("place_detail_component copyFromElementId", () => {
  beforeEach(() => resetConnectionMock());

  it("accepts a copy source without a family and converts unit strings", async () => {
    const tool = setup()("place_detail_component");
    sendCommand.mockResolvedValue(ok);
    await tool.invoke({
      components: [{ copyFromElementId: 4242, start: { x: 0, y: 0 }, end: { x: "10'-0\"", y: "6\"" } }],
    });
    expect(sendCommand).toHaveBeenCalledWith("place_detail_component", {
      components: [{ copyFromElementId: 4242, start: { x: 0, y: 0 }, end: { x: 3048, y: 152.4 } }],
    });
    expect(() => tool.parse({ components: [{ start: { x: 0, y: 0 }, end: { x: 1, y: 0 } }] })).toThrow();
    expect(() => tool.parse({ components: [{ copyFromElementId: 1 }] })).toThrow();
  });
});

describe("create_detail_lines imperial input", () => {
  it("converts point coordinates and radii", () => {
    const tool = setup()("create_detail_lines");
    const parsed = tool.parse({
      lines: [{ center: { x: "1'", y: 0 }, radius: '3"' }],
    }) as any;
    expect(parsed.lines[0].center).toEqual({ x: 304.8, y: 0 });
    expect(parsed.lines[0].radius).toBe(76.2);
  });
});

describe("draw_detail unit strings", () => {
  it("lets unitless strings follow units and unit strings be real lengths", () => {
    const inches = createDetailConverter({ units: "in" });
    expect(inches.point({ x: "1'", y: "6" })).toEqual({ x: 304.8, y: 152.4 });
    expect(inches.length("1 1/2")).toBe(38.1);
    expect(inches.length("25mm")).toBe(25);
    const mm = createDetailConverter({ units: "mm" });
    expect(mm.point({ x: "2'-9\"", y: 10 })).toEqual({ x: 838.2, y: 10 });
  });

  it("rejects unit strings in px mode", () => {
    const px = createDetailConverter({ units: "px", scale: 12 });
    expect(() => px.point({ x: '3"', y: 0 })).toThrow();
  });

  it("parses a draw_detail request with imperial strings", () => {
    const draw = setup()("draw_detail");
    expect(
      draw.parse({
        units: "in",
        lines: [{ kind: "line", start: { x: 0, y: 0 }, end: { x: "2'-0\"", y: 0 } }],
      })
    ).toBeTruthy();
  });
});
