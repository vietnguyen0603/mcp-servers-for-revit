import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerLayoutDetailSheetGridTool } from "../src/tools/layout_detail_sheet_grid.js";
import {
  planGrid,
  splitSheetNumber,
  type MeasuredSheet,
  type MeasuredViewport,
} from "../src/utils/detailSheetGrid.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

// 800 x 600 mm inner border -> 4 x 3 cells of 200 x 200 mm
const BORDER = { x0: 0, y0: 0, x1: 800, y1: 600 };
const OPTIONS = { cols: 4, rows: 3, pad: 4, gap: 3, tries: 20 };

let nextId = 100;
function vp(box: [number, number, number, number], extra: Partial<MeasuredViewport> = {}): MeasuredViewport {
  const id = nextId++;
  return {
    viewportId: id,
    viewId: id + 1000,
    typeId: 7,
    titled: true,
    detailNumber: String(id),
    box,
    titleWidth: 60,
    labelK: [1, 2],
    labelExtra: 10,
    labelHeight: 10,
    ...extra,
  };
}

function sheet(id: number, number: string, viewports: MeasuredViewport[], name = "TYPICAL DETAILS"): MeasuredSheet {
  return { id, number, name, viewports };
}

describe("planGrid", () => {
  it("numbers a detail by the cell holding its title and sets the title line", () => {
    const plan = planGrid([sheet(1, "S8-001", [vp([500, 300, 550, 350])])], BORDER, OPTIONS);
    expect(plan.errors).toEqual([]);
    expect(plan.sheets).toHaveLength(1);
    const [v] = plan.sheets[0].viewports;
    expect(v.detailNumber).toBe("1");
    // line = max(cell - 2 pad, drawing, title) - label extra = 192 - 10
    expect(v.labelLineLength).toBe(182);
    // drawing centred over the title line: x = pad + 192/2; bottom at pad + label height + gap
    expect(v.center).toEqual({ x: 100, y: 400 + 4 + 13 + 25 });
    expect(plan.sheets[0].sheetId).toBe(1);
  });

  it("numbers cells left to right then down when a row is full", () => {
    // 208 mm wide blocks: only three fit across the 800 mm border, the rest wrap to row 2 (cells 5, 6)
    const vps = Array.from({ length: 5 }, () => vp([0, 0, 200, 100]));
    const plan = planGrid([sheet(1, "S8-001", vps)], BORDER, OPTIONS);
    expect(plan.sheets).toHaveLength(1);
    expect(plan.sheets[0].details).toEqual(["1", "2", "3", "5", "6"]);
    expect(plan.sheets[0].occupancy).toEqual(["###.", "##..", "...."]);
  });

  it("numbers No-Title viewports overlapping a titled one as composite sub-views", () => {
    const main = vp([0, 0, 100, 100]);
    const sub = vp([60, 60, 120, 120], { titled: false, titleWidth: null, labelK: null });
    const plan = planGrid([sheet(1, "S8-001", [main, sub])], BORDER, OPTIONS);
    const numbers = Object.fromEntries(plan.sheets[0].viewports.map((v) => [v.viewportId, v.detailNumber]));
    expect(numbers[main.viewportId]).toBe("1");
    expect(numbers[sub.viewportId]).toBe("1A");
    expect(plan.sheets[0].details).toEqual(["1"]);
  });

  it("packs a series onto as few sheets as possible and maps old to new references", () => {
    const a = vp([0, 0, 50, 50], { detailNumber: "3" });
    const b = vp([0, 0, 50, 50], { detailNumber: "1" });
    const plan = planGrid([sheet(1, "S8-001", [a]), sheet(2, "S8-002", [b])], BORDER, OPTIONS);
    expect(plan.sheets.map((s) => s.number)).toEqual(["S8-001"]);
    expect(plan.sheets[0].details).toEqual(["1", "2"]);
    expect(plan.emptiedSheets).toEqual([{ id: 2, number: "S8-002" }]);
    expect(Object.keys(plan.oldToNew)).toContain("1/S8-002");
  });

  it("numbers new sheets from the series start, skipping sheets outside the selection", () => {
    const plan = planGrid(
      [sheet(1, "S8-001", [vp([0, 0, 50, 50])])],
      BORDER,
      OPTIONS,
      [{ prefix: "S8-0", start: 5, name: "NEW DETAILS" }],
      ["S8-005"]
    );
    expect(plan.sheets[0]).toMatchObject({ number: "S8-006", name: "NEW DETAILS", templateSheetId: 1 });
    expect(plan.sheets[0].sheetId).toBeUndefined();
  });

  it("reports details that do not fit one sheet", () => {
    const plan = planGrid([sheet(1, "S8-001", [vp([0, 0, 900, 50])])], BORDER, OPTIONS);
    expect(plan.sheets).toEqual([]);
    expect(plan.errors[0]).toMatch(/more than the 800 x 600 mm border/);
  });

  it("splits sheet numbers into series prefix and suffix", () => {
    expect(splitSheetNumber("S8-001")).toEqual({ prefix: "S8-0", digits: 2, value: 1 });
    expect(splitSheetNumber("S0-300")).toEqual({ prefix: "S0-3", digits: 2, value: 0 });
    expect(splitSheetNumber("A12")).toEqual({ prefix: "A", digits: 2, value: 12 });
  });
});

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerLayoutDetailSheetGridTool(server);
  return getTool("layout_detail_sheet_grid");
}

const measured = {
  Success: true,
  Message: "ok",
  Response: {
    border: BORDER,
    borderSource: "titleBlockFamily",
    sheets: [sheet(1, "S8-001", [vp([0, 0, 50, 50], { detailNumber: "4" })]), sheet(2, "S8-002", [vp([0, 0, 50, 50])])],
    otherSheetNumbers: ["S7-001"],
    warnings: [],
  },
};

function body(result: unknown) {
  return JSON.parse((result as { content: { text: string }[] }).content[0].text);
}

describe("layout_detail_sheet_grid tool", () => {
  beforeEach(() => {
    resetConnectionMock();
  });

  it("requires a sheet selection", async () => {
    const result = (await setup().invoke({ mode: "plan" })) as { isError?: boolean };
    expect(result.isError).toBe(true);
    expect(sendCommand).not.toHaveBeenCalled();
  });

  it("forwards verify with the selection and reference options", async () => {
    sendCommand.mockResolvedValue({ Success: true, Message: "all clear", Response: { allClear: true } });
    await setup().invoke({ mode: "verify", sheetNumberPrefixes: ["S8-0"], familyNameContains: "Section Cut" });
    expect(sendCommand.mock.calls[0][0]).toBe("layout_detail_sheet_grid");
    expect(sendCommand.mock.calls[0][1]).toMatchObject({
      mode: "verify",
      sheetNumberPrefixes: ["S8-0"],
      familyNameContains: "Section Cut",
    });
  });

  it("plans on the server from the measured layout without changing the model", async () => {
    sendCommand.mockResolvedValue(measured);
    const result = body(await setup().invoke({ mode: "plan", sheetNumberPattern: "^S8-0\\d\\d$" }));
    expect(sendCommand).toHaveBeenCalledTimes(1);
    expect(sendCommand.mock.calls[0][1]).toMatchObject({ mode: "measure", sheetNumberPattern: "^S8-0\\d\\d$" });
    expect(result.summary).toMatch(/^1 sheets \(was 2; 0 new, 1 emptied\)/);
    expect(result.sheets[0]).toMatch(/^S8-001 /);
    expect(result.emptiedSheets).toEqual(["S8-002"]);
  });

  it("applies the same plan as a dry run by default", async () => {
    sendCommand.mockResolvedValueOnce(measured).mockResolvedValueOnce({ Success: true, Message: "Dry run", Response: { dryRun: true } });
    const result = body(await setup().invoke({ mode: "apply", sheetIds: [1, 2] }));
    expect(sendCommand).toHaveBeenCalledTimes(2);
    const payload = sendCommand.mock.calls[1][1];
    expect(payload).toMatchObject({ mode: "apply", dryRun: true, deleteEmptySheets: true, remapReferences: true, oldSheetIds: [1, 2] });
    expect(payload.sheets).toHaveLength(1);
    expect(payload.sheets[0]).toMatchObject({ number: "S8-001", sheetId: 1, templateSheetId: 1 });
    expect(payload.sheets[0].viewports).toHaveLength(2);
    expect(result.apply.success).toBe(true);
    expect(result.oldToNew).toBeUndefined();
  });

  it("is registered in the views catalog as destructive", () => {
    expect(TOOL_MANIFEST.layout_detail_sheet_grid).toMatchObject({ catalogs: ["views"], readOnly: false, destructive: true });
  });
});
