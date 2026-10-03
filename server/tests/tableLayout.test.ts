import { describe, expect, it } from "vitest";
import {
  computeTableRules,
  estimateLineCount,
  layoutTable,
  type RuleSegment,
  validateCells,
} from "../src/utils/tableLayout.js";

const key = (s: RuleSegment) => `${s.orientation}${s.line}:${s.from}-${s.to}${s.heavy ? "!" : ""}`;

describe("computeTableRules", () => {
  it("draws every grid line of a plain grid, border heavy", () => {
    const rules = computeTableRules(2, 3, []).map(key);
    expect(rules).toEqual(["h0:0-3!", "h1:0-3", "h2:0-3!", "v0:0-2!", "v1:0-2", "v2:0-2", "v3:0-2!"]);
  });

  it("skips rules inside a column-spanning header", () => {
    const rules = computeTableRules(3, 3, [{ row: 0, col: 0, colSpan: 3, text: "TITLE" }]).map(key);
    // vertical rules start below the header row
    expect(rules.filter((r) => r.startsWith("v"))).toEqual(["v0:0-3!", "v1:1-3", "v2:1-3", "v3:0-3!"]);
    expect(rules.filter((r) => r.startsWith("h"))).toEqual(["h0:0-3!", "h1:0-3", "h2:0-3", "h3:0-3!"]);
  });

  it("splits rules around a block merged in the middle", () => {
    // 4x4 grid, cell (1,1) spans 2x2
    const rules = computeTableRules(4, 4, [{ row: 1, col: 1, rowSpan: 2, colSpan: 2, text: "X" }]).map(key);
    expect(rules).toContain("h2:0-1");
    expect(rules).toContain("h2:3-4");
    expect(rules).not.toContain("h2:0-4");
    expect(rules).toContain("v2:0-1");
    expect(rules).toContain("v2:3-4");
    expect(rules).toContain("h1:0-4");
    expect(rules).toContain("h3:0-4");
  });

  it("uses heavy rules for heavyRows / heavyCols", () => {
    const rules = computeTableRules(3, 2, [], [1], [1]).map(key);
    expect(rules).toContain("h1:0-2!");
    expect(rules).toContain("h2:0-2");
    expect(rules).toContain("v1:0-3!");
  });

  it("handles a row-spanning first column", () => {
    const rules = computeTableRules(3, 2, [{ row: 0, col: 0, rowSpan: 3, text: "A" }]).map(key);
    expect(rules).toContain("h1:1-2");
    expect(rules).toContain("h2:1-2");
    expect(rules).toContain("v1:0-3");
  });
});

describe("validateCells", () => {
  it("rejects out-of-range and overlapping cells", () => {
    expect(validateCells(2, 2, [{ row: 0, col: 0, colSpan: 2, text: "" }])).toEqual([]);
    expect(validateCells(2, 2, [{ row: 1, col: 1, colSpan: 2, text: "" }])[0]).toMatch(/outside/);
    expect(
      validateCells(2, 2, [
        { row: 0, col: 0, rowSpan: 2, text: "" },
        { row: 1, col: 0, text: "" },
      ])[0]
    ).toMatch(/overlaps cells\[0\]/);
  });
});

describe("layoutTable", () => {
  it("places rules and centred text in model mm", () => {
    const layout = layoutTable({
      origin: { x: 100, y: 200 },
      columns: [20, 30],
      rows: [10, 8],
      scale: 10,
      cells: [
        { row: 0, col: 0, colSpan: 2, text: "SCHEDULE" },
        { row: 1, col: 1, text: "#5", align: "Left" },
      ],
      borderStyle: "PEN5",
      ruleStyle: "PEN1",
      textType: '1/8" Arial',
      title: { text: "TABLE 1" },
    });
    expect(layout.width).toBe(500);
    expect(layout.height).toBe(180);
    // top border, full width, heavy
    expect(layout.lines[0]).toEqual({ start: { x: 100, y: 200 }, end: { x: 600, y: 200 }, lineStyle: "PEN5" });
    // middle vertical rule only in row 1
    expect(layout.lines).toContainEqual({ start: { x: 300, y: 100 }, end: { x: 300, y: 20 }, lineStyle: "PEN1" });
    expect(layout.lines).toHaveLength(6);

    const [header, cell, title] = layout.notes;
    expect(header).toMatchObject({ horizontalAlignment: "Center", width: 47, textType: '1/8" Arial', cell: 0 });
    // centred: x mid of 50 mm paper = 25 -> 350 model; one line of 3.175 in a 10 mm row
    expect(header.location.x).toBe(350);
    expect(header.location.y).toBeCloseTo(200 - ((10 - 3.175) / 2) * 10, 6);
    expect(cell.location.x).toBe(100 + 21.5 * 10);
    expect(title).toMatchObject({ text: "TABLE 1", horizontalAlignment: "Center", width: 50, cell: -1 });
    expect(title.location.y).toBeGreaterThan(200);
  });

  it("estimates wrapped line counts", () => {
    expect(estimateLineCount("A", 20, 3.175)).toBe(1);
    expect(estimateLineCount("LINE ONE\nLINE TWO", 50, 3.175)).toBe(2);
    expect(estimateLineCount("WORD ".repeat(20).trim(), 20, 3.175)).toBeGreaterThan(3);
  });
});
