/**
 * Page -> model calibration (similarity transform) and grid bubble detection.
 * Page coordinates: PDF points, y down. Model: millimetres, y up.
 */
import type { PdfPageData, PathItem } from "./pdfPage.js";
import { type Pt, add, canonicalDir, dist, dot, mergeIntervals, perp, scale, sub, unit } from "./geometry.js";

/** model = [a c e; b d f] * [px py 1] */
export interface Transform {
  a: number;
  b: number;
  c: number;
  d: number;
  e: number;
  f: number;
}

export const IDENTITY: Transform = { a: 1, b: 0, c: 0, d: 1, e: 0, f: 0 };

export function toModel(t: Transform, p: Pt): Pt {
  return [t.a * p[0] + t.c * p[1] + t.e, t.b * p[0] + t.d * p[1] + t.f];
}

export function toPage(t: Transform, m: Pt): Pt {
  const det = t.a * t.d - t.b * t.c;
  const x = m[0] - t.e;
  const y = m[1] - t.f;
  return [(t.d * x - t.c * y) / det, (-t.b * x + t.a * y) / det];
}

/** Model millimetres per page unit */
export function scaleOf(t: Transform): number {
  return Math.sqrt(Math.abs(t.a * t.d - t.b * t.c));
}

/** Rotation of the page x axis in model space, degrees */
export function rotationOf(t: Transform): number {
  return (Math.atan2(t.b, t.a) * 180) / Math.PI;
}

/** Similarity transform (scale, rotation, y flip) from two point pairs. */
export function twoPointTransform(pA: Pt, mA: Pt, pB: Pt, mB: Pt): Transform {
  // complex: m = z * conj-flip(p) + t, with p' = (px, -py)
  const qx = pB[0] - pA[0];
  const qy = -(pB[1] - pA[1]);
  const rx = mB[0] - mA[0];
  const ry = mB[1] - mA[1];
  const den = qx * qx + qy * qy;
  if (den < 1e-12) throw new Error("Calibration points A and B coincide on the page");
  const zr = (rx * qx + ry * qy) / den;
  const zi = (ry * qx - rx * qy) / den;
  // m = (zr + i zi) * (px - i py) = (zr px + zi py) + i (zi px - zr py)
  const t: Transform = { a: zr, c: zi, b: zi, d: -zr, e: 0, f: 0 };
  const m0 = toModel(t, pA);
  t.e = mA[0] - m0[0];
  t.f = mA[1] - m0[1];
  return t;
}

export interface Bubble {
  center: Pt;
  diameter: number;
  label: string;
  /** Unit direction (page coords, canonical) of the grid line through the bubble, if found */
  dir: Pt | null;
}

function isCircle(p: PathItem, minSize: number, maxSize: number): boolean {
  if (!p.stroke && !p.fill) return false;
  let curves = 0;
  let lines = 0;
  for (const s of p.subpaths) {
    curves += s.curves;
    lines += s.lines;
  }
  if (curves < 4 || lines > 1) return false;
  const w = p.bbox[2] - p.bbox[0];
  const h = p.bbox[3] - p.bbox[1];
  return Math.abs(w - h) < Math.max(1, 0.05 * w) && w >= minSize && w <= maxSize;
}

export interface PageSeg {
  a: Pt;
  b: Pt;
  w: number;
}

/** Straight stroked segments of the page (curve subpaths skipped). */
export function pageSegments(page: PdfPageData, opts: { minWidth?: number; strokesOnly?: boolean } = {}): PageSeg[] {
  const out: PageSeg[] = [];
  const minW = opts.minWidth ?? 0;
  for (const p of page.paths) {
    if (opts.strokesOnly !== false && !p.stroke) continue;
    if (p.lineWidth < minW) continue;
    for (const s of p.subpaths) {
      if (s.curves > 0) continue;
      for (let i = 0; i + 3 < s.pts.length; i += 2) {
        const a: Pt = [s.pts[i], s.pts[i + 1]];
        const b: Pt = [s.pts[i + 2], s.pts[i + 3]];
        if (a[0] !== b[0] || a[1] !== b[1]) out.push({ a, b, w: p.lineWidth });
      }
    }
  }
  return out;
}

export interface BubbleOptions {
  minSize?: number;
  maxSize?: number;
  labelPattern?: RegExp;
  /** Restrict to this page rectangle [x0,y0,x1,y1] */
  pageRegion?: [number, number, number, number];
}

export function findBubbles(page: PdfPageData, opts: BubbleOptions = {}, segs?: PageSeg[]): Bubble[] {
  const minSize = opts.minSize ?? 10;
  const maxSize = opts.maxSize ?? 60;
  const pat = opts.labelPattern ?? /^[A-Z]{0,3}[-.']?\d{0,3}[A-Z]?'?$/i;
  const reg = opts.pageRegion;
  const circles: { c: Pt; d: number }[] = [];
  for (const p of page.paths) {
    if (!isCircle(p, minSize, maxSize)) continue;
    const c: Pt = [(p.bbox[0] + p.bbox[2]) / 2, (p.bbox[1] + p.bbox[3]) / 2];
    if (reg && !(c[0] >= reg[0] && c[0] <= reg[2] && c[1] >= reg[1] && c[1] <= reg[3])) continue;
    const d = p.bbox[2] - p.bbox[0];
    if (circles.some((q) => dist(q.c, c) < 0.2 * d && Math.abs(q.d - d) < 0.2 * d)) continue; // fill + stroke duplicates
    circles.push({ c, d });
  }
  const bubbles: Bubble[] = [];
  for (const { c, d } of circles) {
    const r = d / 2;
    const inside = page.texts.filter((t) => dist([t.x, t.y], c) < r * 0.8 && t.size >= 0.25 * d && t.size <= 1.2 * d);
    if (!inside.length) continue;
    inside.sort((p, q) => p.ox - q.ox);
    const label = inside.map((t) => t.str).join("").replace(/\s+/g, "");
    if (!label || !pat.test(label)) continue;
    bubbles.push({ center: c, diameter: d, label, dir: null });
  }
  if (bubbles.length) {
    const all = segs ?? pageSegments(page);
    for (const b of bubbles) b.dir = lineDirectionAt(b, all);
  }
  return bubbles;
}

/** Dominant direction of the segments whose infinite line passes through the bubble centre. */
function lineDirectionAt(b: Bubble, segs: PageSeg[]): Pt | null {
  const r = b.diameter / 2;
  const reach = r * 15;
  const tol = Math.max(0.15 * r, 0.3);
  const bins = new Map<number, { w: number; sx: number; sy: number }>();
  for (const s of segs) {
    const da = dist(s.a, b.center);
    const db = dist(s.b, b.center);
    if (Math.min(da, db) > reach) continue;
    const v = sub(s.b, s.a);
    const L = Math.hypot(v[0], v[1]);
    if (L < 0.2) continue;
    // must lie outside the circle
    if (da < r * 0.9 || db < r * 0.9) continue;
    const u = canonicalDir([v[0] / L, v[1] / L]);
    const off = Math.abs(dot(sub(b.center, s.a), perp(u)));
    if (off > tol) continue;
    let ang = (Math.atan2(u[1], u[0]) * 180) / Math.PI;
    if (ang < 0) ang += 180;
    const key = Math.round(ang * 2) % 360;
    const e = bins.get(key) ?? { w: 0, sx: 0, sy: 0 };
    e.w += L;
    e.sx += u[0] * L;
    e.sy += u[1] * L;
    bins.set(key, e);
  }
  let best: { w: number; sx: number; sy: number } | null = null;
  for (const [key, e] of bins) {
    // add neighbours to avoid bin splitting
    const n1 = bins.get((key + 1) % 360);
    const n2 = bins.get((key + 359) % 360);
    const w = e.w + (n1?.w ?? 0) + (n2?.w ?? 0);
    if (!best || w > best.w) best = { w, sx: e.sx, sy: e.sy };
  }
  if (!best || best.w < r) return null;
  return canonicalDir(unit([best.sx, best.sy]));
}

export interface GridLinePage {
  label: string;
  bubbles: Bubble[];
  /** Point on the line and canonical unit direction, page coords */
  p: Pt;
  u: Pt;
  /** Extent along u (dot(point,u)) covering bubbles and the drawn line */
  s0: number;
  s1: number;
  diameter: number;
}

/** Group bubbles by label into grid lines (several lines may share a label on multi-view sheets). */
export function gridLines(bubbles: Bubble[], segs: PageSeg[]): GridLinePage[] {
  const byLabel = new Map<string, Bubble[]>();
  for (const b of bubbles) {
    const arr = byLabel.get(b.label);
    if (arr) arr.push(b);
    else byLabel.set(b.label, [b]);
  }
  const out: GridLinePage[] = [];
  for (const [label, list] of byLabel) {
    const left = [...list].sort((p, q) => (q.dir ? 1 : 0) - (p.dir ? 1 : 0));
    while (left.length) {
      const seed = left.shift()!;
      const r = seed.diameter / 2;
      const members = [seed];
      if (seed.dir) {
        const n = perp(seed.dir);
        for (let i = left.length - 1; i >= 0; i--) {
          const o = left[i];
          if (Math.abs(o.diameter - seed.diameter) > 0.25 * seed.diameter) continue;
          if (Math.abs(dot(sub(o.center, seed.center), n)) < Math.max(r * 0.3, 0.5)) {
            members.push(o);
            left.splice(i, 1);
          }
        }
      }
      let u: Pt | null = seed.dir;
      let p: Pt = seed.center;
      if (members.length >= 2) {
        // direction through the two farthest bubbles
        let best: [Bubble, Bubble] = [members[0], members[1]];
        for (const a of members) for (const b of members) if (dist(a.center, b.center) > dist(best[0].center, best[1].center)) best = [a, b];
        if (dist(best[0].center, best[1].center) > 10 * r) {
          u = canonicalDir(unit(sub(best[1].center, best[0].center)));
          p = scale(add(best[0].center, best[1].center), 0.5);
        }
      }
      if (!u) {
        out.push({ label, bubbles: members, p, u: [0, 0], s0: 0, s1: 0, diameter: seed.diameter });
        continue;
      }
      const throughBubbles = members.length >= 2 && u !== seed.dir;
      let s0 = 0;
      let s1 = 0;
      for (let iter = 0; iter < 3; iter++) {
        const { segs: on, s0: a0, s1: a1 } = lineRun(p, u, members, segs, r);
        s0 = a0;
        s1 = a1;
        if (throughBubbles || on.length < 2) break;
        const fit = fitLine(on);
        if (!fit) break;
        // keep the line through the bubble centre, take the fitted direction
        u = fit.u;
        p = fit.p;
      }
      out.push({ label, bubbles: members, p, u, s0, s1, diameter: seed.diameter });
    }
  }
  return out;
}

/** Segments collinear with the line that belong to the drawn run through the bubbles. */
function lineRun(p: Pt, u: Pt, members: Bubble[], segs: PageSeg[], r: number) {
  const n = perp(u);
  const o = dot(p, n);
  const tol = Math.max(r * 0.15, 0.3);
  const cand: { s: PageSeg; a: number; b: number }[] = [];
  for (const s of segs) {
    if (Math.abs(dot(s.a, n) - o) > tol || Math.abs(dot(s.b, n) - o) > tol) continue;
    const a = dot(s.a, u);
    const b = dot(s.b, u);
    cand.push({ s, a: Math.min(a, b), b: Math.max(a, b) });
  }
  const bs = members.map((m) => dot(m.center, u));
  const iv: [number, number][] = cand.map((c) => [c.a, c.b]);
  for (const s of bs) iv.push([s - r, s + r]);
  const runs = mergeIntervals(iv, r * 4);
  const keep = runs.filter(([a, b]) => bs.some((s) => s >= a - r && s <= b + r));
  const s0 = Math.min(...keep.map((k) => k[0]), ...bs);
  const s1 = Math.max(...keep.map((k) => k[1]), ...bs);
  const on = cand.filter((c) => keep.some(([a, b]) => c.a >= a - 1e-6 && c.b <= b + 1e-6)).map((c) => c.s);
  return { segs: on, s0, s1 };
}

/** Total least squares line through segments (length weighted, exact second moments). */
export function fitLine(segs: { a: Pt; b: Pt }[]): { p: Pt; u: Pt } | null {
  let W = 0;
  let mx = 0;
  let my = 0;
  for (const s of segs) {
    const L = dist(s.a, s.b);
    W += L;
    mx += (L * (s.a[0] + s.b[0])) / 2;
    my += (L * (s.a[1] + s.b[1])) / 2;
  }
  if (W <= 0) return null;
  mx /= W;
  my /= W;
  let sxx = 0;
  let syy = 0;
  let sxy = 0;
  for (const s of segs) {
    const L = dist(s.a, s.b);
    const ax = s.a[0] - mx;
    const ay = s.a[1] - my;
    const bx = s.b[0] - mx;
    const by = s.b[1] - my;
    sxx += (L * (ax * ax + ax * bx + bx * bx)) / 3;
    syy += (L * (ay * ay + ay * by + by * by)) / 3;
    sxy += (L * (2 * ax * ay + ax * by + bx * ay + 2 * bx * by)) / 6;
  }
  const theta = 0.5 * Math.atan2(2 * sxy, sxx - syy);
  return { p: [mx, my], u: canonicalDir([Math.cos(theta), Math.sin(theta)]) };
}

export interface GridCalibrationSpec {
  x?: Record<string, number>;
  y?: Record<string, number>;
}

export interface CalibrationResult {
  transform: Transform;
  mmPerPoint: number;
  rotationDeg: number;
  residuals?: { label: string; axis: "x" | "y"; expected: number; fitted: number; error: number }[];
  method: "points" | "grids";
}

/**
 * Fit model = k*(px, -py) + (tx, ty) from grid labels: x labels are vertical grid
 * lines (page x -> model x), y labels horizontal grid lines (page y -> model y).
 */
export function fitGridCalibration(lines: GridLinePage[], spec: GridCalibrationSpec): CalibrationResult {
  const rows: { kind: "x" | "y"; pv: number; mv: number; label: string }[] = [];
  const pick = (label: string, axis: "x" | "y") => {
    const cands = lines.filter((l) => {
      if (l.label !== label || (l.u[0] === 0 && l.u[1] === 0)) return false;
      // x axis -> vertical page line; y axis -> horizontal
      return axis === "x" ? Math.abs(l.u[0]) < 0.035 : Math.abs(l.u[1]) < 0.035;
    });
    if (!cands.length) {
      const any = lines.filter((l) => l.label === label);
      throw new Error(
        any.length
          ? `Grid ${label}: found but not ${axis === "x" ? "vertical" : "horizontal"} on the page (use it on the other axis or give explicit points)`
          : `Grid ${label}: no grid bubble with this label was found`
      );
    }
    // prefer the biggest bubbles, then the longest drawn line (main plan over key plans)
    cands.sort((p, q) => q.diameter - p.diameter || q.s1 - q.s0 - (p.s1 - p.s0));
    const l = cands[0];
    return axis === "x" ? l.p[0] : l.p[1];
  };
  for (const [label, v] of Object.entries(spec.x ?? {})) rows.push({ kind: "x", pv: pick(label, "x"), mv: v, label });
  for (const [label, v] of Object.entries(spec.y ?? {})) rows.push({ kind: "y", pv: pick(label, "y"), mv: v, label });
  const nx = rows.filter((r) => r.kind === "x").length;
  const ny = rows.filter((r) => r.kind === "y").length;
  if (nx < 1 || ny < 1 || nx + ny < 3) throw new Error("Grid calibration needs at least 2 labels on one axis and 1 on the other");
  // unknowns [k, tx, ty]: x rows: k*px + tx = mx ; y rows: -k*py + ty = my
  const A: number[][] = [
    [0, 0, 0],
    [0, 0, 0],
    [0, 0, 0],
  ];
  const B = [0, 0, 0];
  for (const r of rows) {
    const row = r.kind === "x" ? [r.pv, 1, 0] : [-r.pv, 0, 1];
    for (let i = 0; i < 3; i++) {
      B[i] += row[i] * r.mv;
      for (let j = 0; j < 3; j++) A[i][j] += row[i] * row[j];
    }
  }
  const sol = solve3(A, B);
  if (!sol) throw new Error("Grid calibration is degenerate (labels at the same page position?)");
  const [k, tx, ty] = sol;
  const transform: Transform = { a: k, b: 0, c: 0, d: -k, e: tx, f: ty };
  const residuals = rows.map((r) => {
    const fitted = r.kind === "x" ? k * r.pv + tx : -k * r.pv + ty;
    return { label: r.label, axis: r.kind, expected: r.mv, fitted: Math.round(fitted), error: Math.round((fitted - r.mv) * 10) / 10 };
  });
  return { transform, mmPerPoint: Math.abs(k), rotationDeg: 0, residuals, method: "grids" };
}

function solve3(A: number[][], B: number[]): number[] | null {
  const M = A.map((r, i) => [...r, B[i]]);
  for (let c = 0; c < 3; c++) {
    let p = c;
    for (let r = c + 1; r < 3; r++) if (Math.abs(M[r][c]) > Math.abs(M[p][c])) p = r;
    if (Math.abs(M[p][c]) < 1e-12) return null;
    [M[c], M[p]] = [M[p], M[c]];
    for (let r = 0; r < 3; r++) {
      if (r === c) continue;
      const f = M[r][c] / M[c][c];
      for (let k = c; k < 4; k++) M[r][k] -= f * M[c][k];
    }
  }
  return [M[0][3] / M[0][0], M[1][3] / M[1][1], M[2][3] / M[2][2]];
}
