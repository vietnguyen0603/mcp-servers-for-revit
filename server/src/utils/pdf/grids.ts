/** Grid lines and text extraction in model coordinates. */
import type { GridLinePage } from "./calibration.js";
import { type Pt, add, clipLineToBox, dot, round, scale, sub } from "./geometry.js";
import type { Box, Ctx } from "./context.js";

/**
 * Index of grid lines forming the main plan: the largest group of lines that
 * cross each other within their drawn extents (key plans / sections fall out).
 */
export function mainGridCluster(lines: GridLinePage[]): GridLinePage[] {
  let best = largestCluster(lines);
  // a label appears once per plan: drop weaker duplicates (other views touching the plan), then re-cluster
  const strength = (l: GridLinePage) => l.bubbles.length * 1e6 + (l.s1 - l.s0);
  const byLabel = new Map<string, GridLinePage>();
  for (const l of best) {
    const o = byLabel.get(l.label);
    if (!o || strength(l) > strength(o)) byLabel.set(l.label, l);
  }
  if (byLabel.size < best.length) best = largestCluster([...byLabel.values()]);
  return best;
}

function largestCluster(lines: GridLinePage[]): GridLinePage[] {
  const L = lines.filter((l) => l.u[0] !== 0 || l.u[1] !== 0);
  const n = L.length;
  const parent = L.map((_, i) => i);
  const find = (i: number): number => (parent[i] === i ? i : (parent[i] = find(parent[i])));
  for (let i = 0; i < n; i++) {
    for (let j = i + 1; j < n; j++) {
      const a = L[i];
      const b = L[j];
      const cross = a.u[0] * b.u[1] - a.u[1] * b.u[0];
      if (Math.abs(cross) < 0.05) continue;
      // intersection parameters
      const d = sub(b.p, a.p);
      const ta = (d[0] * b.u[1] - d[1] * b.u[0]) / cross;
      const tb = (d[0] * a.u[1] - d[1] * a.u[0]) / cross;
      const sa = dot(a.p, a.u) + ta;
      const sb = dot(b.p, b.u) + tb;
      const ma = Math.max(a.diameter, (a.s1 - a.s0) * 0.25);
      const mb = Math.max(b.diameter, (b.s1 - b.s0) * 0.25);
      if (sa >= a.s0 - ma && sa <= a.s1 + ma && sb >= b.s0 - mb && sb <= b.s1 + mb) parent[find(i)] = find(j);
    }
  }
  const groups = new Map<number, GridLinePage[]>();
  L.forEach((l, i) => {
    const r = find(i);
    const g = groups.get(r);
    if (g) g.push(l);
    else groups.set(r, [l]);
  });
  let best: GridLinePage[] = [];
  for (const g of groups.values()) if (g.length > best.length) best = g;
  return best;
}

export function lineEndsPage(l: GridLinePage): [Pt, Pt] {
  const base = sub(l.p, scale(l.u, dot(l.p, l.u)));
  return [add(base, scale(l.u, l.s0)), add(base, scale(l.u, l.s1))];
}

/** Model box covering the drawn grid lines (+ margin). */
export function boxFromGrids(ctx: { M(p: Pt): Pt }, lines: GridLinePage[], margin: number): Box | null {
  if (!lines.length) return null;
  let b: Box = [Infinity, Infinity, -Infinity, -Infinity];
  for (const l of lines) {
    for (const e of lineEndsPage(l)) {
      const m = ctx.M(e);
      b = [Math.min(b[0], m[0]), Math.min(b[1], m[1]), Math.max(b[2], m[0]), Math.max(b[3], m[1])];
    }
  }
  return [b[0] - margin, b[1] - margin, b[2] + margin, b[3] + margin];
}

export interface GridOut {
  label: string;
  orientation: "x" | "y" | "inclined";
  /** Model x of a vertical grid / y of a horizontal grid */
  value?: number;
  angleDeg: number;
  start: { x: number; y: number };
  end: { x: number; y: number };
  bubbles: { x: number; y: number }[];
}

export function extractGrids(ctx: Ctx, lines: GridLinePage[], clipToBox: boolean): GridOut[] {
  const out: GridOut[] = [];
  for (const l of lines) {
    if (l.u[0] === 0 && l.u[1] === 0) continue;
    const [pa, pb] = lineEndsPage(l);
    let a = ctx.M(pa);
    let b = ctx.M(pb);
    const bubbles = l.bubbles.map((bb) => ctx.M(bb.center));
    if (!bubbles.some((p) => ctx.inBox(p)) && !(ctx.inBox(a) || ctx.inBox(b))) continue;
    let u = ctx.dirM(l.u);
    // orient start -> end left-to-right / bottom-to-top
    if (u[0] < -1e-9 || (Math.abs(u[0]) <= 1e-9 && u[1] < 0)) u = [-u[0], -u[1]];
    if (dot(sub(b, a), u) < 0) [a, b] = [b, a];
    if (clipToBox) {
      const c = clipLineToBox(a, u, ctx.box);
      if (c) [a, b] = c;
    }
    const ang = (Math.atan2(u[1], u[0]) * 180) / Math.PI;
    const vertical = Math.abs(u[0]) < 1e-3;
    const horizontal = Math.abs(u[1]) < 1e-3;
    const g: GridOut = {
      label: l.label,
      orientation: vertical ? "x" : horizontal ? "y" : "inclined",
      angleDeg: round(ang, 3),
      start: { x: round(a[0]), y: round(a[1]) },
      end: { x: round(b[0]), y: round(b[1]) },
      bubbles: bubbles.map((p) => ({ x: round(p[0]), y: round(p[1]) })),
    };
    if (vertical) g.value = round((a[0] + b[0]) / 2);
    if (horizontal) g.value = round((a[1] + b[1]) / 2);
    out.push(g);
  }
  out.sort((p, q) => p.orientation.localeCompare(q.orientation) || (p.value ?? 0) - (q.value ?? 0) || p.label.localeCompare(q.label));
  return out;
}

/** Where an (inclined) grid crosses a horizontal model line y = y0 */
export function crossAtY(g: GridOut, y0: number): number | null {
  const dy = g.end.y - g.start.y;
  if (Math.abs(dy) < 1e-9) return null;
  return g.start.x + ((y0 - g.start.y) * (g.end.x - g.start.x)) / dy;
}

export interface TextOut {
  text: string;
  x: number;
  y: number;
  angleDeg: number;
  sizeMm: number;
}

export function extractText(ctx: Ctx, filter?: RegExp): TextOut[] {
  return ctx
    .texts()
    .filter((t) => ctx.inBox(t.p) && (!filter || filter.test(t.text)))
    .map((t) => ({ text: t.text, x: round(t.p[0]), y: round(t.p[1]), angleDeg: round(t.angleDeg, 2), sizeMm: round(t.size) }));
}


