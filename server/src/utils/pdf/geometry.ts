/** Small 2D geometry helpers used by the PDF extractors (model or page units). */

export type Pt = [number, number];

export interface Seg {
  a: Pt;
  b: Pt;
  /** Stroke width in page units */
  w: number;
}

export const dot = (a: Pt, b: Pt) => a[0] * b[0] + a[1] * b[1];
export const sub = (a: Pt, b: Pt): Pt => [a[0] - b[0], a[1] - b[1]];
export const add = (a: Pt, b: Pt): Pt => [a[0] + b[0], a[1] + b[1]];
export const scale = (a: Pt, s: number): Pt => [a[0] * s, a[1] * s];
export const len = (a: Pt) => Math.hypot(a[0], a[1]);
export const dist = (a: Pt, b: Pt) => Math.hypot(a[0] - b[0], a[1] - b[1]);
export const perp = (u: Pt): Pt => [-u[1], u[0]];
export const unit = (a: Pt): Pt => {
  const l = len(a) || 1;
  return [a[0] / l, a[1] / l];
};
export const round = (v: number, d = 0) => {
  const f = 10 ** d;
  return Math.round(v * f) / f;
};

/** Canonical direction: x > 0, or x == 0 and y > 0. */
export function canonicalDir(u: Pt): Pt {
  if (u[0] < -1e-9 || (Math.abs(u[0]) <= 1e-9 && u[1] < 0)) return [-u[0], -u[1]];
  return u;
}

export function polygonArea(poly: Pt[]): number {
  let s = 0;
  for (let i = 0, n = poly.length; i < n; i++) {
    const p = poly[i];
    const q = poly[(i + 1) % n];
    s += p[0] * q[1] - q[0] * p[1];
  }
  return s / 2;
}

export function pointInPolygon(p: Pt, poly: Pt[]): boolean {
  let inside = false;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    const [xi, yi] = poly[i];
    const [xj, yj] = poly[j];
    if (yi > p[1] !== yj > p[1] && p[0] < ((xj - xi) * (p[1] - yi)) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}

/** Distance from p to segment ab */
export function distToSegment(p: Pt, a: Pt, b: Pt): number {
  const v = sub(b, a);
  const L2 = dot(v, v);
  const t = L2 > 0 ? Math.max(0, Math.min(1, dot(sub(p, a), v) / L2)) : 0;
  return dist(p, add(a, scale(v, t)));
}

export function convexHull(points: Pt[]): Pt[] {
  const pts = [...points].sort((a, b) => a[0] - b[0] || a[1] - b[1]);
  if (pts.length < 3) return pts;
  const cross = (o: Pt, a: Pt, b: Pt) => (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
  const lower: Pt[] = [];
  for (const p of pts) {
    while (lower.length >= 2 && cross(lower[lower.length - 2], lower[lower.length - 1], p) <= 0) lower.pop();
    lower.push(p);
  }
  const upper: Pt[] = [];
  for (let i = pts.length - 1; i >= 0; i--) {
    const p = pts[i];
    while (upper.length >= 2 && cross(upper[upper.length - 2], upper[upper.length - 1], p) <= 0) upper.pop();
    upper.push(p);
  }
  upper.pop();
  lower.pop();
  return lower.concat(upper);
}

export interface RotRect {
  center: Pt;
  /** Unit vector of the first side */
  u: Pt;
  /** Length along u */
  w: number;
  /** Length along perp(u) */
  h: number;
  area: number;
}

/** Minimum-area enclosing rectangle (rotating the hull edges). */
export function minAreaRect(points: Pt[]): RotRect {
  const hull = convexHull(points);
  let best: RotRect | null = null;
  const n = hull.length;
  for (let i = 0; i < Math.max(n, 1); i++) {
    const a = hull[i];
    const b = hull[(i + 1) % n] ?? a;
    const e = sub(b, a);
    const u: Pt = len(e) > 0 ? unit(e) : [1, 0];
    const v = perp(u);
    let s0 = Infinity;
    let s1 = -Infinity;
    let t0 = Infinity;
    let t1 = -Infinity;
    for (const p of hull) {
      const s = dot(p, u);
      const t = dot(p, v);
      s0 = Math.min(s0, s);
      s1 = Math.max(s1, s);
      t0 = Math.min(t0, t);
      t1 = Math.max(t1, t);
    }
    const area = (s1 - s0) * (t1 - t0);
    if (!best || area < best.area - 1e-9) {
      const cs = (s0 + s1) / 2;
      const ct = (t0 + t1) / 2;
      best = { center: add(scale(u, cs), scale(v, ct)), u, w: s1 - s0, h: t1 - t0, area };
    }
  }
  return best ?? { center: [0, 0], u: [1, 0], w: 0, h: 0, area: 0 };
}

function dpOpen(pts: Pt[], eps: number, from: number, to: number, keep: boolean[]) {
  let maxD = -1;
  let idx = -1;
  for (let i = from + 1; i < to; i++) {
    const d = distToSegment(pts[i], pts[from], pts[to]);
    if (d > maxD) {
      maxD = d;
      idx = i;
    }
  }
  if (idx >= 0 && maxD > eps) {
    keep[idx] = true;
    dpOpen(pts, eps, from, idx, keep);
    dpOpen(pts, eps, idx, to, keep);
  }
}

/** Douglas-Peucker simplification of a closed ring (no repeated end point). */
export function simplifyClosed(ring: Pt[], eps: number): Pt[] {
  const n = ring.length;
  if (n <= 3) return ring.slice();
  let far = 0;
  let fd = -1;
  for (let i = 1; i < n; i++) {
    const d = dist(ring[0], ring[i]);
    if (d > fd) {
      fd = d;
      far = i;
    }
  }
  const pts = [...ring, ring[0]];
  const keep = new Array(pts.length).fill(false);
  keep[0] = keep[far] = keep[n] = true;
  dpOpen(pts, eps, 0, far, keep);
  dpOpen(pts, eps, far, n, keep);
  const out: Pt[] = [];
  for (let i = 0; i < n; i++) if (keep[i]) out.push(ring[i]);
  return out;
}

/** Clip the infinite line p + t*u to an axis-aligned box. Returns null if it misses. */
export function clipLineToBox(p: Pt, u: Pt, box: [number, number, number, number]): [Pt, Pt] | null {
  let t0 = -Infinity;
  let t1 = Infinity;
  const lo = [box[0], box[1]];
  const hi = [box[2], box[3]];
  for (let k = 0; k < 2; k++) {
    if (Math.abs(u[k]) < 1e-12) {
      if (p[k] < lo[k] || p[k] > hi[k]) return null;
    } else {
      const a = (lo[k] - p[k]) / u[k];
      const b = (hi[k] - p[k]) / u[k];
      t0 = Math.max(t0, Math.min(a, b));
      t1 = Math.min(t1, Math.max(a, b));
    }
  }
  if (t0 > t1) return null;
  return [add(p, scale(u, t0)), add(p, scale(u, t1))];
}

/** Clip a segment to a box (Liang-Barsky). */
export function clipSegmentToBox(a: Pt, b: Pt, box: [number, number, number, number]): [Pt, Pt] | null {
  const d = sub(b, a);
  const L = len(d);
  if (L === 0) return box[0] <= a[0] && a[0] <= box[2] && box[1] <= a[1] && a[1] <= box[3] ? [a, b] : null;
  const u = scale(d, 1 / L);
  const r = clipLineToBox(a, u, box);
  if (!r) return null;
  const t0 = Math.max(0, dot(sub(r[0], a), u));
  const t1 = Math.min(L, dot(sub(r[1], a), u));
  if (t0 > t1) return null;
  return [add(a, scale(u, t0)), add(a, scale(u, t1))];
}

export interface Line {
  /** Canonical unit direction */
  u: Pt;
  /** Unit normal = perp(u) */
  n: Pt;
  /** Signed offset of the line: dot(point, n) */
  o: number;
  /** Merged intervals along u (dot(point,u)), sorted */
  runs: [number, number][];
  /** Raw member intervals */
  parts: [number, number, number][]; // [s0, s1, width]
  /** Max stroke width of members */
  w: number;
}

export interface MergeOptions {
  /** Max perpendicular distance of a segment's end points to the line */
  distTol: number;
  /** Max angle difference in radians */
  angTol: number;
  /** Gap bridged when merging runs along the line */
  gap: number;
}

/**
 * Group segments into infinite lines by direction + distance-to-line (not by
 * rounded angle/offset, which splits nearly-collinear dashes far from the
 * origin), then merge the intervals along each line (dashed lines -> runs).
 * Candidate lines are found through spatial tiles holding their members.
 */
export function mergeCollinear(segs: { a: Pt; b: Pt; w?: number }[], opt: MergeOptions & { tile?: number }): Line[] {
  const lines: Line[] = [];
  const tile = opt.tile ?? Math.max(opt.gap * 2, opt.distTol * 40, 1e-6);
  const tiles = new Map<string, number[]>();
  const keyOf = (p: Pt) => `${Math.floor(p[0] / tile)}|${Math.floor(p[1] / tile)}`;
  const register = (p: Pt, id: number) => {
    const k = keyOf(p);
    const arr = tiles.get(k);
    if (!arr) tiles.set(k, [id]);
    else if (arr[arr.length - 1] !== id && !arr.includes(id)) arr.push(id);
  };
  const sinTol = Math.sin(opt.angTol);
  // longest first so that line directions come from long pieces
  const order = segs
    .map((s, i) => ({ i, L: dist(s.a, s.b) }))
    .filter((s) => s.L > 0)
    .sort((x, y) => y.L - x.L);
  for (const { i, L: segLen } of order) {
    const s = segs[i];
    const u = canonicalDir(unit(sub(s.b, s.a)));
    let hit = -1;
    let bestD = Infinity;
    const seen = new Set<number>();
    for (const p of [s.a, s.b]) {
      const tx = Math.floor(p[0] / tile);
      const ty = Math.floor(p[1] / tile);
      for (let dx = -1; dx <= 1; dx++)
        for (let dy = -1; dy <= 1; dy++) {
          const ids = tiles.get(`${tx + dx}|${ty + dy}`);
          if (!ids) continue;
          for (const id of ids) {
            if (seen.has(id)) continue;
            seen.add(id);
            const L = lines[id];
            if (Math.abs(L.u[0] * u[1] - L.u[1] * u[0]) > sinTol) continue;
            const da = Math.abs(dot(s.a, L.n) - L.o);
            const db = Math.abs(dot(s.b, L.n) - L.o);
            if (da > opt.distTol || db > opt.distTol) continue;
            if (da + db < bestD) {
              bestD = da + db;
              hit = id;
            }
          }
        }
    }
    const w = s.w ?? 0;
    if (hit < 0) {
      const n = perp(u);
      lines.push({ u, n, o: dot(s.a, n), runs: [], parts: [], w: 0 });
      hit = lines.length - 1;
    }
    const L = lines[hit];
    // register the member along its length so long pieces are found from any tile they cross
    const steps = Math.max(1, Math.ceil(segLen / tile));
    for (let k = 0; k <= steps; k++) register(add(s.a, scale(sub(s.b, s.a), k / steps)), hit);
    const s0 = dot(s.a, L.u);
    const s1 = dot(s.b, L.u);
    L.parts.push([Math.min(s0, s1), Math.max(s0, s1), w]);
    if (w > L.w) L.w = w;
  }
  for (const L of lines) L.runs = mergeIntervals(L.parts.map((p) => [p[0], p[1]]), opt.gap);
  return lines;
}

export function mergeIntervals(iv: [number, number][], gap: number): [number, number][] {
  const s = [...iv].sort((a, b) => a[0] - b[0]);
  const out: [number, number][] = [];
  for (const [a, b] of s) {
    const last = out[out.length - 1];
    if (last && a <= last[1] + gap) last[1] = Math.max(last[1], b);
    else out.push([a, b]);
  }
  return out;
}

export function linePoint(L: { u: Pt; n: Pt; o: number }, s: number): Pt {
  return add(scale(L.u, s), scale(L.n, L.o));
}
