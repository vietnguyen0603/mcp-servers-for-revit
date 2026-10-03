import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () => await import("./helpers/connectionMock.js"));

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerDrawDetailTool } from "../src/tools/draw_detail.js";
import { registerCreateDraftedTableTool } from "../src/tools/create_drafted_table.js";
import { resetConnectionMock, sendCommand, withRevitConnection } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

type Params = Record<string, any>;

/** Fake Revit: answers each batch command with sequential ids, PascalCase envelope like AIResult. */
function fakeRevit(options: { failLineIndexes?: number[]; activeView?: { Id: number; Scale: number } } = {}) {
  let nextId = 1000;
  const batch = (items: any[], make: (item: any, index: number) => Params | string) => ({
    Success: true,
    Message: "ok",
    Response: {
      succeeded: 0,
      failed: 0,
      results: items.map((item, index) => {
        const made = make(item, index);
        return typeof made === "string" ? { index, success: false, message: made } : { index, success: true, ...made };
      }),
    },
  });
  sendCommand.mockImplementation(async (command: string, params: Params) => {
    switch (command) {
      case "get_current_view_info":
        return options.activeView ?? { Id: 77, Scale: 12 };
      case "list_views":
        return { Success: true, Response: { views: [{ id: 55, scale: 24 }] } };
      case "create_view":
        return batch(params.views, () => ({ viewId: 500, scale: params.views[0].scale }));
      case "modify_annotations":
        return batch(params.operations, () => ({ action: "setParameters" }));
      case "create_filled_region":
        return batch(params.regions, () => ({ filledRegionId: nextId++ }));
      case "create_detail_lines":
        return batch(params.lines, (line, index) =>
          options.failLineIndexes?.includes(index)
            ? "bad line"
            : { detailCurveIds: line.points ? line.points.slice(1).map(() => nextId++) : [nextId++] }
        );
      case "place_detail_component":
        return batch(params.components, () => ({ instanceId: nextId++ }));
      case "place_annotation_symbol":
        return batch(params.symbols, () => ({ elementId: nextId++ }));
      case "create_text_note":
        return batch(params.notes, () => ({ textNoteId: nextId++ }));
      case "create_dimensions":
        return batch(params.dimensions, () => ({ id: nextId++, warnings: [] }));
      default:
        throw new Error(`unexpected command ${command}`);
    }
  });
}

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerDrawDetailTool(server);
  registerCreateDraftedTableTool(server);
  return { draw: getTool("draw_detail"), table: getTool("create_drafted_table") };
}

const commands = () => sendCommand.mock.calls.map((call) => call[0]);
const paramsOf = (command: string) => sendCommand.mock.calls.filter((call) => call[0] === command).map((call) => call[1]);

describe("draw_detail", () => {
  beforeEach(() => resetConnectionMock());

  it("is an annotate write tool", () => {
    expect(TOOL_MANIFEST.draw_detail).toMatchObject({ catalogs: ["annotate"], readOnly: false });
    expect(TOOL_MANIFEST.create_drafted_table).toMatchObject({ catalogs: ["annotate"], readOnly: false });
  });

  it("rejects viewId with createView and empty requests without connecting", async () => {
    const { draw } = setup();
    expect((await draw.invoke({ viewId: 5, createView: { name: "A", scale: 12 }, lines: [] })).isError).toBe(true);
    expect((await draw.invoke({})).isError).toBe(true);
    expect(withRevitConnection).not.toHaveBeenCalled();
  });

  it("validates element shapes", () => {
    const { draw } = setup();
    expect(() => draw.parse({ lines: [{ kind: "line", start: { x: 0, y: 0 } }] })).toThrow();
    expect(() => draw.parse({ lines: [{ kind: "spline", points: [] }] })).toThrow();
    expect(() => draw.parse({ dimensions: [{ refs: [{ tag: "a" }], through: { x: 0, y: 0 }, direction: "h" }] })).toThrow();
    expect(() => draw.parse({ units: "cm" })).toThrow();
    expect(draw.parse({ lines: [{ kind: "arc", center: { x: 0, y: 0 }, radius: 1 }] })).toBeTruthy();
  });

  it("builds a detail in order with inches, tags and dimension references", async () => {
    fakeRevit();
    const { draw } = setup();
    const response = await draw.invoke({
      createView: { name: "FTG", scale: 12, detailLevel: "Fine", titleOnSheet: "FOOTING" },
      units: "in",
      regions: [{ tag: "conc", points: [{ x: 0, y: 0 }, { x: 24, y: 0 }, { x: 24, y: 12 }], type: "Concrete" }],
      lines: [
        { kind: "poly", tag: "ftg", points: [{ x: 0, y: 0 }, { x: 24, y: 0 }, { x: 24, y: 12 }, { x: 0, y: 12 }], closed: true, filletRadius: 0.5, lineStyle: "PEN5" },
        { kind: "arc", tag: "bar", center: { x: 3, y: 3 }, radius: 0.25, lineStyle: "PEN3" },
      ],
      components: [{ tag: "brk", family: "TT-Break Line", type: "Std", start: { x: 0, y: 12 }, end: { x: 24, y: 12 } }],
      symbols: [{ family: "Subtitle", at: { x: 0, y: -12 }, parameters: { Detail: "A" }, leaders: [{ end: { x: 1, y: 1 } }] }],
      texts: [{ text: "#5 CONT.", at: { x: 30, y: 10 }, type: '1/8" Arial', width: 40, leaders: [{ end: { x: 12, y: 6 }, side: "Left" }] }],
      dimensions: [
        {
          refs: [{ tag: "ftg", index: 0, end: "start" }, { tag: "ftg", index: 0, end: "end" }],
          through: { x: 0, y: -6 },
          direction: "h",
          type: "Tick",
          text: { override: "2'-0\"", position: { x: 12, y: -8 } },
        },
        { refs: [{ tag: "nope" }, { elementId: 9 }], through: { x: 0, y: 0 }, direction: "v" },
      ],
    });

    expect(commands()).toEqual([
      "create_view",
      "modify_annotations",
      "create_filled_region",
      "create_detail_lines",
      "place_detail_component",
      "place_annotation_symbol",
      "create_text_note",
      "create_dimensions",
    ]);
    expect(paramsOf("create_view")[0]).toEqual({ views: [{ viewType: "Drafting", name: "FTG", scale: 12, detailLevel: "Fine" }] });
    expect(paramsOf("modify_annotations")[0]).toEqual({
      operations: [{ action: "setParameters", elementIds: [500], parameters: { "Title on Sheet": "FOOTING" } }],
    });

    const region = paramsOf("create_filled_region")[0];
    expect(region.viewId).toBe(500);
    expect(region.regions[0]).toEqual({
      boundary: [{ x: 0, y: 0 }, { x: 609.6, y: 0 }, { x: 609.6, y: 304.8 }],
      filledRegionTypeName: "Concrete",
    });

    const lines = paramsOf("create_detail_lines")[0];
    expect(lines.viewId).toBe(500);
    expect(lines.lines[0]).toMatchObject({ closed: true, filletRadius: 12.7, lineStyle: "PEN5" });
    expect(lines.lines[1]).toEqual({ center: { x: 76.2, y: 76.2 }, radius: 6.35, startAngleDeg: 0, endAngleDeg: 360, lineStyle: "PEN3" });

    expect(paramsOf("place_detail_component")[0].components[0]).toEqual({
      familyName: "TT-Break Line",
      typeName: "Std",
      start: { x: 0, y: 304.8 },
      end: { x: 609.6, y: 304.8 },
    });
    expect(paramsOf("place_annotation_symbol")[0].symbols[0]).toMatchObject({
      familyName: "Subtitle",
      location: { x: 0, y: -304.8 },
      leaders: [{ end: { x: 25.4, y: 25.4 } }],
    });
    const note = paramsOf("create_text_note")[0].notes[0];
    expect(note).toEqual({
      text: "#5 CONT.",
      location: { x: 762, y: 254 },
      viewId: 500,
      textNoteTypeName: '1/8" Arial',
      width: 40,
      leaders: [{ end: { x: 304.8, y: 152.4 }, side: "Left" }],
    });

    const dims = paramsOf("create_dimensions")[0].dimensions;
    // the unknown-tag dimension is never sent
    expect(dims).toHaveLength(1);
    const result = JSON.parse(response.content[0].text);
    const polyIds: number[] = result.tags.ftg;
    expect(polyIds).toHaveLength(3);
    expect(dims[0]).toEqual({
      viewId: 500,
      startPoint: { x: 0, y: -152.4 },
      endPoint: { x: 100, y: -152.4 },
      linePoint: { x: 0, y: -152.4 },
      references: [
        { elementId: polyIds[0], end: "start" },
        { elementId: polyIds[0], end: "end" },
      ],
      dimensionType: "Tick",
      text: { override: "2'-0\"", position: { x: 304.8, y: -203.2 } },
    });

    expect(response.isError).toBeUndefined();
    expect(result.viewId).toBe(500);
    expect(result.created).toEqual({
      regions: "1/1",
      lines: "2/2",
      components: "1/1",
      symbols: "1/1",
      texts: "1/1",
      dimensions: "1/2",
    });
    expect(Object.keys(result.tags).sort()).toEqual(["bar", "brk", "conc", "ftg"]);
    expect(result.failures).toEqual([{ kind: "dimensions", index: 1, message: expect.stringMatching(/Unknown tag 'nope'/) }]);
  });

  it("converts px with the active view scale, y flipped, and chunks large batches", async () => {
    fakeRevit({ activeView: { Id: 77, Scale: 12 }, failLineIndexes: [3] });
    const { draw } = setup();
    const lines = Array.from({ length: 160 }, (_, i) => ({
      kind: "line" as const,
      start: { x: 100, y: 100 + i },
      end: { x: 1100, y: 100 + i },
    }));
    const response = await draw.invoke({ units: "px", origin: { x: 100, y: 100 }, snapIn: 0.25, lines });
    expect(commands()).toEqual(["get_current_view_info", "create_detail_lines", "create_detail_lines"]);
    const [first, second] = paramsOf("create_detail_lines");
    expect(first.viewId).toBe(77);
    expect(first.lines).toHaveLength(150);
    expect(second.lines).toHaveLength(10);
    // 1000 px at 166.667 px/in and 1:12 = 72" real
    expect(first.lines[0]).toEqual({ start: { x: 0, y: 0 }, end: { x: 1828.8, y: 0 } });
    // +10 px down = 18.288 mm real, snapped to 0.75" (19.05 mm), y negative
    expect(first.lines[10].start).toEqual({ x: 0, y: -19.05 });
    // the fake fails chunk-local index 3 in each chunk: items 3 and 150 + 3 map back to their source indexes
    const result = JSON.parse(response.content[0].text);
    expect(result.created.lines).toBe("158/160");
    expect(result.failures.map((f: any) => f.index)).toEqual([3, 153]);
    expect(response.isError).toBeUndefined();
  });

  it("reads the scale of an explicit view for px mode and flags total failure", async () => {
    fakeRevit({ failLineIndexes: [0] });
    const { draw } = setup();
    const response = await draw.invoke({ viewId: 55, units: "px", lines: [{ kind: "line", start: { x: 0, y: 0 }, end: { x: 100, y: 0 } }] });
    expect(commands()).toEqual(["list_views", "create_detail_lines"]);
    expect(paramsOf("create_detail_lines")[0].lines[0].end.x).toBeCloseTo((100 / (7000 / 42)) * 24 * 25.4, 5);
    expect(response.isError).toBe(true);
  });
});

describe("create_drafted_table", () => {
  beforeEach(() => resetConnectionMock());

  it("rejects overlapping merged cells before connecting", async () => {
    const { table } = setup();
    const response = await table.invoke({
      origin: { x: 0, y: 0 },
      columns: [10, 10],
      rows: [5, 5],
      cells: [
        { row: 0, col: 0, colSpan: 2, text: "A" },
        { row: 0, col: 1, text: "B" },
      ],
    });
    expect(response.isError).toBe(true);
    expect(withRevitConnection).not.toHaveBeenCalled();
  });

  it("draws rules then texts at the view scale", async () => {
    fakeRevit({ activeView: { Id: 77, Scale: 10 } });
    const { table } = setup();
    const response = await table.invoke({
      origin: { x: 0, y: 0 },
      columns: [20, 30],
      rows: [8, 6],
      cells: [
        { row: 0, col: 0, colSpan: 2, text: "SCHEDULE" },
        { row: 1, col: 0, text: "A1", textType: '3/32" Arial' },
      ],
      title: { text: "TABLE" },
    });
    expect(commands()).toEqual(["get_current_view_info", "create_detail_lines", "create_text_note"]);
    const lines = paramsOf("create_detail_lines")[0];
    expect(lines.viewId).toBe(77);
    expect(lines.lines).toHaveLength(6);
    expect(lines.lines.filter((l: any) => l.lineStyle === "PEN5")).toHaveLength(4);
    expect(lines.lines.filter((l: any) => l.lineStyle === "PEN1")).toHaveLength(2);
    const notes = paramsOf("create_text_note")[0].notes;
    expect(notes).toHaveLength(3);
    expect(notes[0]).toMatchObject({ text: "SCHEDULE", viewId: 77, textNoteTypeName: '1/8" Arial', horizontalAlignment: "Center", width: 47 });
    expect(notes[0].location.x).toBe(250);
    expect(notes[1].textNoteTypeName).toBe('3/32" Arial');
    const result = JSON.parse(response.content[0].text);
    expect(result).toMatchObject({ viewId: 77, scale: 10, size: { widthMm: 500, heightMm: 140 }, created: { lines: "6/6", texts: "3/3" } });
    expect(result.textNoteIds).toHaveLength(3);
  });
});
