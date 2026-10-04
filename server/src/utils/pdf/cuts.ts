/**
 * Columns and walls from section-cut fills (port of the vert.py raster approach,
 * but the fills are rasterised from the vectors with their clip paths applied).
 */
import { type Pt, add, dist, dot, len, minAreaRect, perp, round, scale, simplifyClosed, sub, unit } from "./geometry.js";
import { Ctx, ModelRaster, PointIndex, autoRes, nearestLabel, rasterizeFill, type Box } from "./context.js";
import { Mask, boundaryLoops, close, connectedComponents, fillPolygon, open } from "./raster.js";
import type { PathItem } from "./pdfPage.js";

export interface CutsOptions {
  /** Fill gray 0..1 (null/undefined = auto) */
  fillGray?: number;
  grayTolerance?: number;
  /** Raster resolution in mm per pixel (default 10, coarsened for big boxes) */
  resolution?: number;
  minThickness?: number;
  maxThickness?: number;
  columnMaxAspect?: number;
  labelPattern?: RegExp;
  labelMaxDist?: number;
  includePolygons?: boolean;
}

export interface ColumnOut {
  x: number;
  y: number;
  b: number;
  h: number;
  rotationDeg: number;
  mark: string | null;
}

export interface WallOut {
  start: { x: number; y: number };
  end: { x: number; y: number };
  thickness: number;
  fallback?: boolean;
}

const grayOf = (p: PathItem): number | null => {
  const c = p.fillColor;
  if (!c || Math.abs(c[0] - c[1]) > 3 || Math.abs(c[1] - c[2]) > 3) return null;
  return (c[0] + c[1] + c[2]) / 3 / 255;
};

/** Area-weighted histogram of gray fills in the box (2-decimal bins). */
export function grayHistogram(ctx: Ctx): { gray: number; areaM2: number; count: number }[] {
  const h = new Map<number, { a: number; n: number }>();
  for (const p of ctx.page.paths) {
    if (!p.fill) continue;
    const g = grayOf(p);
    if (g === null || g < 0.15 || g > 0.95) continue;
    const c = ctx.M([(p.bbox[0] + p.bbox[2]) / 2, (p.bbox[1] + p.bbox[3]) / 2]);
    if (!ctx.inBox(c)) continue;
    const a = (p.bbox[2] - p.bbox[0]) * (p.bbox[3] - p.bbox[1]) * ctx.k * ctx.k;
    const key = Math.round(g * 100) / 100;
    const e = h.get(key) ?? { a: 0, n: 0 };
    e.a += a;
    e.n++;
    h.set(key, e);
  }
  return [...h.entries()]
    .map(([gray, e]) => ({ gray, areaM2: round(e.a / 1e6, 1), count: e.n }))
    .sort((p, q) => q.areaM2 - p.areaM2);
}

export function extractCuts(ctx: Ctx, opt: CutsOptions = {}) {
  const hist = grayHistogram(ctx);
  const gray = opt.fillGray ?? hist[0]?.gray;
  if (gray === undefined) return { fillGray: null, grayHistogram: hist, columns: [], walls: [], warnings: ["No gray fills found in the box"] };
  const tol = opt.grayTolerance ?? 6 / 255;
  const fills = ctx.page.paths.filter((p) => {
    if (!p.fill) return false;
    const g = grayOf(p);
    return g !== null && Math.abs(g - gray) <= tol;
  });
  // raster extent: matching fills inside the box (+ margin)
  let ext: Box = [Infinity, Infinity, -Infinity, -Infinity];
  const used: PathItem[] = [];
  for (const p of fills) {
    const a = ctx.M([p.bbox[0], p.bbox[1]]);
    const b = ctx.M([p.bbox[2], p.bbox[3]]);
    const lo: Pt = [Math.min(a[0], b[0]), Math.min(a[1], b[1])];
    const hi: Pt = [Math.max(a[0], b[0]), Math.max(a[1], b[1])];
    if (hi[0] < ctx.box[0] || lo[0] > ctx.box[2] || hi[1] < ctx.box[1] || lo[1] > ctx.box[3]) continue;
    used.push(p);
    ext = [Math.min(ext[0], lo[0]), Math.min(ext[1], lo[1]), Math.max(ext[2], hi[0]), Math.max(ext[3], hi[1])];
  }
  if (!used.length) return { fillGray: gray, grayHistogram: hist, columns: [], walls: [], warnings: ["No fills of that gray in the box"] };
  const margin = 500;
  ext = [
    Math.max(ext[0], ctx.box[0]) - margin,
    Math.max(ext[1], ctx.box[1]) - margin,
    Math.min(ext[2], ctx.box[2]) + margin,
    Math.min(ext[3], ctx.box[3]) + margin,
  ];
  const res = autoRes(ext, opt.resolution ?? 10);
  const R = new ModelRaster(ext, res);
  for (const p of used) rasterizeFill(R, ctx, p);
  const m = close(R.mask, 1);

  // vector end points used to snap polygon vertices (cut outlines are ~0.96pt)
  const vp: Pt[] = [];
  for (const s of ctx.modelSegs({ minWidth: 0.7, inBoxMargin: 0 })) vp.push(s.a, s.b);
  const index = new PointIndex(vp, 100);
  const snap = (p: Pt, tol = 90): Pt => index.nearest(p, tol) ?? p;

  const cc = connectedComponents(m, true);
  const minArea = 0.05e6 / (res * res);
  const keep = new Set<number>();
  for (let i = 1; i <= cc.count; i++) if (cc.area[i] >= minArea) keep.add(i);
  const loops = boundaryLoops(cc.labels, m.w, m.h, keep);
  const byLabel = new Map<number, typeof loops>();
  for (const l of loops) {
    const a = byLabel.get(l.label);
    if (a) a.push(l);
    else byLabel.set(l.label, [l]);
  }
  const labelRe = opt.labelPattern ?? /^(C\d+[A-Z]?\d?|CW\d+[A-Z]?|WB?\d*)$/;
  const clabs = ctx.labels(labelRe, 3000);
  const maxAspect = opt.columnMaxAspect ?? 4.01;
  const columns: ColumnOut[] = [];
  const walls: WallOut[] = [];
  const polygons: { x: number; y: number }[][] = [];
  const eps = Math.max(3 * res, 25);
  for (const [label, ls] of byLabel) {
    ls.sort((p, q) => q.area - p.area);
    const outer = ls[0];
    const toM = (pts: Pt[]) => pts.map((q) => R.toModel(q));
    const outerM = simplifyClosed(toM(outer.pts), eps);
    const poly = outerM.map((q) => snap(q));
    const holes = ls
      .slice(1)
      .filter((h) => h.area < 0 && -h.area * res * res > 1e6)
      .map((h) => simplifyClosed(toM(h.pts), eps).map((q) => snap(q)));
    const area = cc.area[label] * res * res;
    const rr = minAreaRect(toM(outer.pts));
    const fillRatio = area / Math.max(rr.w * rr.h, 1);
    if (opt.includePolygons) polygons.push(poly.map((q) => ({ x: round(q[0]), y: round(q[1]) })));
    if (poly.length === 4 && fillRatio > 0.9 && Math.max(rr.w, rr.h) / Math.max(Math.min(rr.w, rr.h), 1) < maxAspect) {
      columns.push(columnFromQuad(poly, clabs, opt.labelMaxDist ?? 2500));
    } else {
      // component mask (for the inside test)
      const pad = 4;
      const [bx0, by0, bx1, by1] = cc.bbox[label];
      const x0 = bx0 - pad;
      const y0 = by0 - pad;
      const sub = new ModelRaster([ext[0] + x0 * res, ext[3] - (by1 + pad + 1) * res, ext[0] + (bx1 + pad + 1) * res, ext[3] - y0 * res], res);
      const cm = sub.mask;
      for (let y = 0; y < cm.h; y++) {
        const yy = y + y0;
        if (yy < 0 || yy >= m.h) continue;
        for (let x = 0; x < cm.w; x++) {
          const xx = x + x0;
          if (xx >= 0 && xx < m.w && cc.labels[yy * m.w + xx] === label) cm.data[y * cm.w + x] = 1;
        }
      }
      const ws = decompose(poly, holes, (p) => sub.near(p), opt.minThickness ?? 150, opt.maxThickness ?? 1900);
      walls.push(...ws, ...residual(cm, ws, sub));
    }
  }
  const out: any = {
    fillGray: round(gray, 3),
    grayHistogram: hist.slice(0, 6),
    resolutionMm: round(res, 1),
    columns: columns.sort((p, q) => q.y - p.y || p.x - q.x),
    walls,
  };
  if (opt.includePolygons) out.polygons = polygons;
  return out;
}

function columnFromQuad(q: Pt[], labels: ReturnType<Ctx["labels"]>, maxDist: number): ColumnOut {
  const e = [0, 1, 2, 3].map((i) => sub(q[(i + 1) % 4], q[i]));
  const L = e.map((v) => len(v));
  // side lengths: mean of opposite sides; rotation: length-weighted mean of the 4 edge angles mod 90
  const L1 = (L[0] + L[2]) / 2;
  const L2 = (L[1] + L[3]) / 2;
  let sx = 0;
  let sy = 0;
  for (let i = 0; i < 4; i++) {
    const a4 = 4 * Math.atan2(e[i][1], e[i][0]);
    sx += L[i] * Math.cos(a4);
    sy += L[i] * Math.sin(a4);
  }
  let rot = (Math.atan2(sy, sx) / 4) * (180 / Math.PI); // (-45, 45]
  if (rot >= 45) rot -= 90;
  const ctr: Pt = [(q[0][0] + q[1][0] + q[2][0] + q[3][0]) / 4, (q[0][1] + q[1][1] + q[2][1] + q[3][1]) / 4];
  const ex: Pt = [Math.cos((rot * Math.PI) / 180), Math.sin((rot * Math.PI) / 180)];
  const b = Math.abs(dot(scale(e[0], 1 / L[0]), ex)) > 0.7 ? L1 : L2;
  const h = b === L1 ? L2 : L1;
  return { x: round(ctr[0]), y: round(ctr[1]), b: round(b), h: round(h), rotationDeg: round(rot, 3), mark: nearestLabel(labels, ctr, maxDist) };
}

interface Cand {
  p0: Pt;
  p1: Pt;
  t: number;
  L: number;
}

/** Pair parallel opposite edges of a cut polygon into wall centre lines. */
export function decompose(poly: Pt[], holes: Pt[][], inside: (p: Pt) => boolean, tmin = 150, tmax = 1900): WallOut[] {
  const E: [Pt, Pt][] = [];
  for (const pl of [poly, ...holes]) for (let i = 0; i < pl.length; i++) E.push([pl[i], pl[(i + 1) % pl.length]]);
  const cand: Cand[] = [];
  for (let i = 0; i < E.length; i++) {
    const [a0, a1] = E[i];
    const da = sub(a1, a0);
    const La = len(da);
    if (La < 100) continue;
    const ua = scale(da, 1 / La);
    const na = perp(ua);
    for (let j = i + 1; j < E.length; j++) {
      const [b0, b1] = E[j];
      const db = sub(b1, b0);
      const Lb = len(db);
      if (Lb < 100) continue;
      const ub = scale(db, 1 / Lb);
      if (Math.abs(Math.abs(dot(ua, ub)) - 1) > 2e-4) continue;
      const t = dot(sub(b0, a0), na);
      if (!(tmin <= Math.abs(t) && Math.abs(t) <= tmax)) continue;
      const s = [dot(sub(b0, a0), ua), dot(sub(b1, a0), ua)].sort((x, y) => x - y);
      const lo = Math.max(0, s[0]);
      const hi = Math.min(La, s[1]);
      if (hi - lo < Math.min(Math.abs(t), 300) * 0.9) continue;
      const ok = [0.1, 0.3, 0.5, 0.7, 0.9].every((f) => inside(add(add(a0, scale(ua, lo + (hi - lo) * f)), scale(na, t / 2))));
      if (!ok) continue;
      cand.push({ p0: add(add(a0, scale(ua, lo)), scale(na, t / 2)), p1: add(add(a0, scale(ua, hi)), scale(na, t / 2)), t: Math.abs(t), L: hi - lo });
    }
  }
  cand.sort((p, q) => p.t - q.t);
  const keep: Cand[] = [];
  for (const c of cand) {
    let dup = false;
    for (const k of keep) {
      const u = unit(sub(k.p1, k.p0));
      if (Math.abs(Math.abs(dot(u, scale(sub(c.p1, c.p0), 1 / c.L))) - 1) > 1e-3) continue;
      const nrm = perp(u);
      if (Math.abs(dot(sub(c.p0, k.p0), nrm)) < (k.t + c.t) / 2) {
        const s0 = dot(sub(c.p0, k.p0), u);
        const s1 = dot(sub(c.p1, k.p0), u);
        if (Math.min(s0, s1) < k.L - 50 && Math.max(s0, s1) > 50) {
          dup = true;
          break;
        }
      }
    }
    if (!dup) keep.push(c);
  }
  // extend ends to meet perpendicular walls
  for (const c of keep) {
    for (const end of ["p0", "p1"] as const) {
      const pt = c[end];
      let u = scale(sub(c.p1, c.p0), 1 / c.L);
      if (end === "p0") u = scale(u, -1);
      for (const o of keep) {
        if (o === c) continue;
        const v = scale(sub(o.p1, o.p0), 1 / o.L);
        const nv = perp(v);
        if (Math.abs(dot(u, v)) > 0.5) continue;
        const d = dot(sub(o.p0, pt), nv) / dot(u, nv);
        if (d > 0 && d <= o.t / 2 + 20) {
          const proj = dot(sub(add(pt, scale(u, d)), o.p0), v);
          if (proj >= -o.t && proj <= o.L + o.t) c[end] = add(pt, scale(u, d));
        }
      }
    }
  }
  return keep.map((c) => ({
    start: { x: round(c.p0[0]), y: round(c.p0[1]) },
    end: { x: round(c.p1[0]), y: round(c.p1[1]) },
    thickness: Math.round(c.t / 5) * 5,
  }));
}

/** Leftover fill not explained by the paired walls -> rectangle fit fallback walls. */
function residual(cm: Mask, ws: WallOut[], R: ModelRaster): WallOut[] {
  const res = R.res;
  const r = new Mask(cm.w, cm.h, cm.data.slice());
  const wm = new Mask(cm.w, cm.h);
  for (const w of ws) {
    const a: Pt = [w.start.x, w.start.y];
    const b: Pt = [w.end.x, w.end.y];
    const L = dist(a, b);
    if (L < 1) continue;
    const u = scale(sub(b, a), 1 / L);
    const n = perp(u);
    const h = w.thickness / 2 + 60;
    const pts = [
      add(sub(a, scale(u, h / 2)), scale(n, h)),
      add(add(b, scale(u, h / 2)), scale(n, h)),
      sub(add(b, scale(u, h / 2)), scale(n, h)),
      sub(sub(a, scale(u, h / 2)), scale(n, h)),
    ].map((q) => R.toPx(q));
    fillPolygon(wm, [pts]);
  }
  for (let i = 0; i < r.data.length; i++) if (wm.data[i]) r.data[i] = 0;
  const ro = open(r, 2);
  const cc = connectedComponents(ro, true);
  const out: WallOut[] = [];
  const keep = new Set<number>();
  for (let i = 1; i <= cc.count; i++) if (cc.area[i] * res * res >= 0.25e6) keep.add(i);
  if (!keep.size) return out;
  const loops = boundaryLoops(cc.labels, ro.w, ro.h, keep);
  for (const lab of keep) {
    const ls = loops.filter((l) => l.label === lab && l.area > 0).sort((p, q) => q.area - p.area);
    if (!ls.length) continue;
    const rr = minAreaRect(ls[0].pts.map((q) => R.toModel(q)));
    const w = rr.w;
    const h = rr.h;
    if (Math.max(w, h) < 2 * Math.min(w, h) || Math.min(w, h) < 120) continue;
    const u = w >= h ? rr.u : perp(rr.u);
    const L = Math.max(w, h);
    const t = Math.min(w, h);
    const a = sub(rr.center, scale(u, L / 2));
    const b = add(rr.center, scale(u, L / 2));
    out.push({ start: { x: round(a[0]), y: round(a[1]) }, end: { x: round(b[0]), y: round(b[1]) }, thickness: Math.round(t / 50) * 50, fallback: true });
  }
  return out;
}

