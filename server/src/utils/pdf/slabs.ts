/** Slab outline (flood fill of heavy strokes) and "X"-marked openings (port of slabs.py). */
import { type Line, type Pt, dist, dot, len, linePoint, mergeCollinear, perp, pointInPolygon, polygonArea, round, scale, simplifyClosed, sub, add } from "./geometry.js";
import { type Ctx, ModelRaster, autoRes } from "./context.js";
import { Mask, boundaryLoops, connectedComponents, drawLine, floodOutside } from "./raster.js";

export interface SlabOptions {
  /** Minimum stroke width (page points) of the slab edge lines */
  minStrokeWidth?: number;
  resolution?: number;
}

export function extractSlabOutline(ctx: Ctx, opt: SlabOptions = {}) {
  const minW = opt.minStrokeWidth ?? 0.55;
  const res = autoRes(ctx.box, opt.resolution ?? 10);
  const R = new ModelRaster(ctx.box, res);
  const thick = 3;
  for (const p of ctx.page.paths) {
    if (!p.stroke || p.lineWidth < minW || p.dashed) continue;
    for (const s of p.subpaths) {
      for (let i = 0; i + 3 < s.pts.length; i += 2) {
        const a = ctx.M([s.pts[i], s.pts[i + 1]]);
        const b = ctx.M([s.pts[i + 2], s.pts[i + 3]]);
        if (!ctx.inBox(a, 1000) && !ctx.inBox(b, 1000)) continue;
        drawLine(R.mask, R.toPx(a), R.toPx(b), thick);
      }
    }
  }
  const outside = floodOutside(R.mask);
  const inside = new Mask(R.mask.w, R.mask.h);
  for (let i = 0; i < inside.data.length; i++) inside.data[i] = outside.data[i] ? 0 : 1;
  const cc = connectedComponents(inside, true);
  if (!cc.count) return { outline: [], areaM2: 0, warnings: ["No closed outline found"] };
  let best = 1;
  for (let i = 2; i <= cc.count; i++) if (cc.area[i] > cc.area[best]) best = i;
  const loops = boundaryLoops(cc.labels, inside.w, inside.h, new Set([best])).filter((l) => l.area > 0);
  loops.sort((p, q) => q.area - p.area);
  const ring = simplifyClosed(
    loops[0].pts.map((q) => R.toModel(q)),
    4 * res
  );
  const warnings: string[] = [];
  const bb = cc.bbox[best];
  if (bb[0] === 0 || bb[1] === 0 || bb[2] === inside.w - 1 || bb[3] === inside.h - 1) warnings.push("Outline touches the box edge: the slab edge is not closed (or the box is too small)");
  // counter-clockwise order (model y up)
  if (polygonArea(ring) < 0) ring.reverse();
  return {
    outline: ring.map((q) => ({ x: round(q[0], -1), y: round(q[1], -1) })),
    areaM2: round((cc.area[best] * res * res) / 1e6, 1),
    resolutionMm: round(res, 1),
    warnings,
  };
}

export interface OpeningOptions {
  minSize?: number;
  /** Polygon (model) the openings must lie inside */
  outline?: Pt[];
}

/** Rectangles marked by a dashed "X": two equal diagonals crossing at their midpoints. */
export function extractOpenings(ctx: Ctx, opt: OpeningOptions = {}) {
  const raw: { a: Pt; b: Pt }[] = [];
  for (const s of ctx.modelSegs({ inBoxMargin: 0 })) {
    const v = sub(s.b, s.a);
    const L = len(v);
    if (L < 20) continue;
    let ang = (Math.atan2(v[1], v[0]) * 180) / Math.PI;
    ang = ((ang % 180) + 180) % 180;
    const m90 = ang % 90;
    if (Math.min(m90, 90 - m90) < 8) continue;
    raw.push({ a: s.a, b: s.b });
  }
  const lines = mergeCollinear(raw, { distTol: 30, angTol: (1.5 * Math.PI) / 180, gap: 800 });
  const quads = findXs(lines, 600);
  const minSize = opt.minSize ?? 400;
  const outline = opt.outline;
  const accepted: Pt[][] = [];
  const rejected = { small: 0, outside: 0, overlap: 0 };
  for (const q of quads) {
    const s1 = dist(q[0], q[1]);
    const s2 = dist(q[1], q[2]);
    if (Math.min(s1, s2) < minSize || Math.abs(polygonArea(q)) < minSize * minSize * 2.5) {
      rejected.small++;
      continue;
    }
    if (outline && outline.length >= 3) {
      const c = scale(add(q[0], q[2]), 0.5);
      const shrunk = q.map((p) => {
        const d = sub(c, p);
        const L = len(d);
        return add(p, scale(d, Math.min(50 * Math.SQRT2, L) / L));
      });
      if (!shrunk.every((p) => pointInPolygon(p, outline))) {
        rejected.outside++;
        continue;
      }
    }
    if (accepted.some((o) => convexOverlap(o, q))) {
      rejected.overlap++;
      continue;
    }
    accepted.push(q);
  }
  return {
    openings: accepted.map((q) => {
      const xs = q.map((p) => p[0]);
      const ys = q.map((p) => p[1]);
      return {
        polygon: q.map((p) => ({ x: round(p[0]), y: round(p[1]) })),
        bbox: { minX: round(Math.min(...xs)), minY: round(Math.min(...ys)), maxX: round(Math.max(...xs)), maxY: round(Math.max(...ys)) },
        areaM2: round(Math.abs(polygonArea(q)) / 1e6, 2),
      };
    }),
    candidates: quads.length,
    rejected,
  };
}

interface RunInfo {
  line: Line;
  s0: number;
  s1: number;
  /** End points of the member pieces inside the run (along line.u) */
  ends: number[];
  bb: [number, number, number, number];
}

/**
 * Find "X" marks: two crossing runs whose member pieces end symmetrically at the
 * same distance h from the crossing on all four arms (equal diagonals bisecting
 * each other). Extra collinear pieces beyond the corners do not break the match.
 */
export function findXs(lines: Line[], minDiagonal: number): Pt[][] {
  const runs: RunInfo[] = [];
  for (const line of lines) {
    for (const [s0, s1] of line.runs) {
      if (s1 - s0 < minDiagonal) continue;
      const ends: number[] = [];
      for (const [a, b] of line.parts) if (a >= s0 - 1e-6 && b <= s1 + 1e-6) ends.push(a, b);
      const A = linePoint(line, s0);
      const B = linePoint(line, s1);
      runs.push({ line, s0, s1, ends, bb: [Math.min(A[0], B[0]), Math.min(A[1], B[1]), Math.max(A[0], B[0]), Math.max(A[1], B[1])] });
    }
  }
  const out: Pt[][] = [];
  for (let i = 0; i < runs.length; i++) {
    const r1 = runs[i];
    for (let j = i + 1; j < runs.length; j++) {
      const r2 = runs[j];
      if (r1.bb[2] < r2.bb[0] || r2.bb[2] < r1.bb[0] || r1.bb[3] < r2.bb[1] || r2.bb[3] < r1.bb[1]) continue;
      const u1 = r1.line.u;
      const u2 = r2.line.u;
      const cr = u1[0] * u2[1] - u1[1] * u2[0];
      if (Math.abs(cr) < 0.17) continue; // must cross at > ~10 deg
      // intersection of the two infinite lines
      const p1 = linePoint(r1.line, 0);
      const p2 = linePoint(r2.line, 0);
      const d = sub(p2, p1);
      const t = (d[0] * u2[1] - d[1] * u2[0]) / cr;
      const c = add(p1, scale(u1, t));
      const t1 = dot(c, u1);
      const t2 = dot(c, u2);
      if (t1 <= r1.s0 || t1 >= r1.s1 || t2 <= r2.s0 || t2 >= r2.s1) continue;
      const arms = [
        r1.ends.filter((e) => e < t1).map((e) => t1 - e),
        r1.ends.filter((e) => e > t1).map((e) => e - t1),
        r2.ends.filter((e) => e < t2).map((e) => t2 - e),
        r2.ends.filter((e) => e > t2).map((e) => e - t2),
      ];
      let best = -1;
      for (const h of arms[0]) {
        if (h * 2 < minDiagonal || h <= best) continue;
        const tol = Math.max(40, 0.02 * h);
        if (arms.slice(1).every((arm) => arm.some((x) => Math.abs(x - h) <= tol))) best = h;
      }
      if (best < 0) continue;
      const pts = [add(c, scale(u1, -best)), add(c, scale(u2, -best)), add(c, scale(u1, best)), add(c, scale(u2, best))].sort(
        (p, q) => Math.atan2(p[1] - c[1], p[0] - c[0]) - Math.atan2(q[1] - c[1], q[0] - c[0])
      );
      out.push(pts);
    }
  }
  return out;
}

/** Separating-axis overlap test for convex polygons (touching counts as no overlap). */
export function convexOverlap(a: Pt[], b: Pt[]): boolean {
  for (const poly of [a, b]) {
    for (let i = 0; i < poly.length; i++) {
      const n = perp(sub(poly[(i + 1) % poly.length], poly[i]));
      let amin = Infinity;
      let amax = -Infinity;
      let bmin = Infinity;
      let bmax = -Infinity;
      for (const p of a) {
        const d = dot(p, n);
        amin = Math.min(amin, d);
        amax = Math.max(amax, d);
      }
      for (const p of b) {
        const d = dot(p, n);
        bmin = Math.min(bmin, d);
        bmax = Math.max(bmax, d);
      }
      const eps = 1e-6 * len(n);
      if (amax <= bmin + eps || bmax <= amin + eps) return false;
    }
  }
  return true;
}
