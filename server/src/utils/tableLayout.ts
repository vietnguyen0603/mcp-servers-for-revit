/**
 * Geometry for drafted tables (create_drafted_table): which grid-line
 * segments to draw around merged cells, and where to put cell text.
 * Pure functions; all inputs in paper millimetres unless noted.
 */

import { MM_PER_INCH, type Point2, textHeightFromTypeName } from "./detailUnits.js";

export type CellAlign = "Left" | "Center" | "Right";

export interface TableCell {
  row: number;
  col: number;
  text: string;
  rowSpan?: number;
  colSpan?: number;
  align?: CellAlign;
  textType?: string;
}

/**
 * One rule segment on a grid line. `line` is the grid-line index (horizontal:
 * 0 = top edge .. rows = bottom edge; vertical: 0 = left .. cols = right);
 * the segment covers cells [from, to) along the line.
 */
export interface RuleSegment {
  orientation: "h" | "v";
  line: number;
  from: number;
  to: number;
  heavy: boolean;
}

/** Problems with cell positions/spans (out of range, overlapping merges). */
export function validateCells(rows: number, cols: number, cells: TableCell[]): string[] {
  const errors: string[] = [];
  const owner = new Map<string, number>();
  cells.forEach((cell, index) => {
    const rowSpan = cell.rowSpan ?? 1;
    const colSpan = cell.colSpan ?? 1;
    if (cell.row < 0 || cell.col < 0 || cell.row + rowSpan > rows || cell.col + colSpan > cols) {
      errors.push(`cells[${index}] (row ${cell.row}, col ${cell.col}, span ${rowSpan}x${colSpan}) is outside the ${rows}x${cols} grid`);
      return;
    }
    for (let r = cell.row; r < cell.row + rowSpan; r++) {
      for (let c = cell.col; c < cell.col + colSpan; c++) {
        const key = `${r},${c}`;
        const other = owner.get(key);
        if (other !== undefined) {
          errors.push(`cells[${index}] overlaps cells[${other}] at row ${r}, col ${c}`);
          return;
        }
        owner.set(key, index);
      }
    }
  });
  return errors;
}

/**
 * Rule segments for a rows x cols grid, skipping the parts of interior grid
 * lines that run through merged cells. Collinear pieces are joined. The outer
 * border and the rules listed in heavyRows (top rule of that row) / heavyCols
 * (left rule of that column) are heavy.
 */
export function computeTableRules(
  rows: number,
  cols: number,
  cells: TableCell[],
  heavyRows: number[] = [],
  heavyCols: number[] = []
): RuleSegment[] {
  const merged = cells.filter((cell) => (cell.rowSpan ?? 1) > 1 || (cell.colSpan ?? 1) > 1);
  const insideH = (line: number, col: number) =>
    merged.some(
      (m) => m.row < line && m.row + (m.rowSpan ?? 1) > line && m.col <= col && m.col + (m.colSpan ?? 1) > col
    );
  const insideV = (line: number, row: number) =>
    merged.some(
      (m) => m.col < line && m.col + (m.colSpan ?? 1) > line && m.row <= row && m.row + (m.rowSpan ?? 1) > row
    );

  const segments: RuleSegment[] = [];
  const sweep = (orientation: "h" | "v", lineCount: number, length: number, covered: (line: number, at: number) => boolean, heavySet: Set<number>) => {
    for (let line = 0; line <= lineCount; line++) {
      const heavy = line === 0 || line === lineCount || heavySet.has(line);
      let start = -1;
      for (let at = 0; at <= length; at++) {
        const draw = at < length && !covered(line, at);
        if (draw && start < 0) start = at;
        if (!draw && start >= 0) {
          segments.push({ orientation, line, from: start, to: at, heavy });
          start = -1;
        }
      }
    }
  };
  sweep("h", rows, cols, insideH, new Set(heavyRows));
  sweep("v", cols, rows, insideV, new Set(heavyCols));
  return segments;
}

/** Cumulative offsets: [0, a, a+b, ...]. */
export function cumulative(sizes: number[]): number[] {
  const out = [0];
  for (const size of sizes) out.push(out[out.length - 1] + size);
  return out;
}

export const DEFAULT_TEXT_HEIGHT_MM = MM_PER_INCH / 8;
/** Average character width as a fraction of text height (Arial, mixed caps). */
const CHAR_WIDTH_FACTOR = 0.65;
/** Baseline-to-baseline pitch as a fraction of text height. */
export const LINE_PITCH_FACTOR = 1.5;

/** Estimated number of wrapped lines for `text` in a box `width` paper mm wide. */
export function estimateLineCount(text: string, width: number, textHeight: number): number {
  const perLine = Math.max(1, Math.floor(width / (CHAR_WIDTH_FACTOR * textHeight)));
  let count = 0;
  for (const paragraph of text.split(/\r?\n/)) {
    let current = 0;
    let lines = 1;
    for (const word of paragraph.split(/\s+/).filter(Boolean)) {
      const length = word.length;
      if (current === 0) {
        current = length;
      } else if (current + 1 + length <= perLine) {
        current += 1 + length;
      } else {
        lines++;
        current = length;
      }
      while (current > perLine) {
        lines++;
        current -= perLine;
      }
    }
    count += lines;
  }
  return Math.max(1, count);
}

export interface TableSpec {
  origin: Point2; // model mm, top-left
  columns: number[]; // paper mm
  rows: number[]; // paper mm
  scale: number;
  cells: TableCell[];
  borderStyle: string;
  ruleStyle: string;
  heavyRows?: number[];
  heavyCols?: number[];
  textType: string;
  textHeightMm?: number; // paper mm override
  paddingMm?: number; // paper mm
  title?: { text: string; textType?: string };
}

export interface TableLine {
  start: Point2;
  end: Point2;
  lineStyle: string;
}

export interface TableNote {
  text: string;
  location: Point2;
  width: number; // paper mm
  horizontalAlignment: CellAlign;
  textType: string;
  /** Source cell index, or -1 for the title. */
  cell: number;
}

export interface TableLayout {
  lines: TableLine[];
  notes: TableNote[];
  /** Overall model size (mm). */
  width: number;
  height: number;
}

function round(value: number): number {
  const rounded = Math.round(value * 1e6) / 1e6;
  return Object.is(rounded, -0) ? 0 : rounded;
}

export function layoutTable(spec: TableSpec): TableLayout {
  const { origin, scale } = spec;
  const xs = cumulative(spec.columns);
  const ys = cumulative(spec.rows);
  const toModel = (paperX: number, paperY: number): Point2 => ({
    x: round(origin.x + paperX * scale),
    y: round(origin.y - paperY * scale),
  });

  const lines = computeTableRules(spec.rows.length, spec.columns.length, spec.cells, spec.heavyRows, spec.heavyCols).map(
    (segment): TableLine => {
      const lineStyle = segment.heavy ? spec.borderStyle : spec.ruleStyle;
      return segment.orientation === "h"
        ? { start: toModel(xs[segment.from], ys[segment.line]), end: toModel(xs[segment.to], ys[segment.line]), lineStyle }
        : { start: toModel(xs[segment.line], ys[segment.from]), end: toModel(xs[segment.line], ys[segment.to]), lineStyle };
    }
  );

  const padding = spec.paddingMm ?? 1.5;
  const notes: TableNote[] = [];
  spec.cells.forEach((cell, index) => {
    if (!cell.text) return;
    const textType = cell.textType ?? spec.textType;
    const height = spec.textHeightMm ?? textHeightFromTypeName(textType) ?? DEFAULT_TEXT_HEIGHT_MM;
    const left = xs[cell.col];
    const right = xs[cell.col + (cell.colSpan ?? 1)];
    const top = ys[cell.row];
    const bottom = ys[cell.row + (cell.rowSpan ?? 1)];
    const width = Math.max(1, right - left - 2 * padding);
    const lineCount = estimateLineCount(cell.text, width, height);
    const blockHeight = height + (lineCount - 1) * height * LINE_PITCH_FACTOR;
    const textTop = top + Math.max(0, (bottom - top - blockHeight) / 2);
    const align = cell.align ?? "Center";
    const anchorX = align === "Left" ? left + padding : align === "Right" ? right - padding : (left + right) / 2;
    notes.push({
      text: cell.text,
      location: toModel(anchorX, textTop),
      width: round(width),
      horizontalAlignment: align,
      textType,
      cell: index,
    });
  });

  const totalWidth = xs[xs.length - 1];
  if (spec.title?.text) {
    const textType = spec.title.textType ?? spec.textType;
    const height = textHeightFromTypeName(textType) ?? DEFAULT_TEXT_HEIGHT_MM;
    // Title box sits one text height + 2 mm (paper) above the top border.
    notes.push({
      text: spec.title.text,
      location: toModel(totalWidth / 2, -(height + 2)),
      width: round(totalWidth),
      horizontalAlignment: "Center",
      textType,
      cell: -1,
    });
  }

  return {
    lines,
    notes,
    width: round(totalWidth * scale),
    height: round(ys[ys.length - 1] * scale),
  };
}
