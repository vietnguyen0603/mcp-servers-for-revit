/** Closed rectangles of given sizes (e.g. barrette piles) from merged collinear edges (port of piles.py). */
import { type Pt, add, dist, dot, linePoint, mergeCollinear, perp, round, scale, sub } from "./geometry.js";
import { type Ctx, nearestLabel } from "./context.js";

export interface RectOptions {
  /** [length, width] pairs in mm, e.g. [[2800,1200],[2800,1500]] */
  sizes: [number, number][];
  tolerance?: number;
  minStrokeWidth?: number;
  labelPattern?: RegExp;
  labelMaxDist?: number;
}

export interface RectOut {
  x: number;
  y: number;
  length: number;
  width: number;
  /** Direction of the long side, (-90, 90] */
  rotationDeg: number;
  size: string;
  mark: string | null;
}

export function extractRects(ctx: Ctx, opt: RectOptions): { rects: RectOut[] } {
  const tol = opt.tolerance ?? 70;
  const maxSide = Math.max(...opt.sizes.flat()) + tol;
  const raw = ctx
    .modelSegs({ minWidth: opt.minStrokeWidth ?? 0.4, inBoxMargin: 0 })
    .filter((s) => {
      const L = dist(s.a, s.b);
      return L > 20 && L < maxSide + 200;
    });
  const lines = mergeCollinear(raw, { distTol: 15, angTol: (0.5 * Math.PI) / 180, gap: 30 });
  const sides = [...new Set(opt.sizes.flat())];
  // edge runs whose length matches one of the side lengths
  const runs: { A: Pt; B: Pt; u: Pt; L: number }[] = [];
  for (const l of lines)
    for (const [s0, s1] of l.runs) {
      const L = s1 - s0;
      if (sides.some((s) => Math.abs(L - s) <= tol)) runs.push({ A: linePoint(l, s0), B: linePoint(l, s1), u: l.u, L });
    }
  const labels = opt.labelPattern ? ctx.labels(opt.labelPattern, 5000) : [];
  const out: RectOut[] = [];
  for (let i = 0; i < runs.length; i++) {
    const r1 = runs[i];
    const n = perp(r1.u);
    for (let j = i + 1; j < runs.length; j++) {
      const r2 = runs[j];
      if (Math.abs(Math.abs(dot(r1.u, r2.u)) - 1) > 2e-3) continue;
      if (Math.abs(r1.L - r2.L) > tol) continue;
      const d = Math.abs(dot(sub(r2.A, r1.A), n));
      const size = opt.sizes.find(
        ([a, b]) => (Math.abs(r1.L - a) <= tol && Math.abs(d - b) <= tol) || (Math.abs(r1.L - b) <= tol && Math.abs(d - a) <= tol)
      );
      if (!size) continue;
      const s = [dot(sub(r2.A, r1.A), r1.u), dot(sub(r2.B, r1.A), r1.u)].sort((x, y) => x - y);
      if (Math.abs(s[0]) > 60 || Math.abs(s[1] - r1.L) > 60) continue;
      const c = scale(add(add(r1.A, r1.B), add(r2.A, r2.B)), 0.25);
      if (out.some((q) => dist(c, [q.x, q.y]) < Math.min(...size) / 4)) continue;
      const longAlongRun = r1.L >= d;
      const u = longAlongRun ? r1.u : n;
      let ang = (Math.atan2(u[1], u[0]) * 180) / Math.PI;
      ang = ((((ang + 90) % 180) + 180) % 180) - 90;
      if (ang === -90) ang = 90;
      out.push({
        x: round(c[0]),
        y: round(c[1]),
        length: round(Math.max(r1.L, d)),
        width: round(Math.min(r1.L, d)),
        rotationDeg: round(ang, 2),
        size: `${size[0]}x${size[1]}`,
        mark: labels.length ? nearestLabel(labels, c, opt.labelMaxDist ?? 3500) : null,
      });
    }
  }
  return { rects: out };
}
