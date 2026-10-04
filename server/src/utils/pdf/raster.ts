/**
 * Minimal binary raster toolkit in typed arrays (no native deps):
 * polygon scanline fill, thick lines, morphology, flood fill, connected
 * components and exact pixel-edge boundary loops.
 */
import type { Pt } from "./geometry.js";

export class Mask {
  readonly data: Uint8Array;
  constructor(readonly w: number, readonly h: number, data?: Uint8Array) {
    this.data = data ?? new Uint8Array(w * h);
  }
  get(x: number, y: number): number {
    return x >= 0 && y >= 0 && x < this.w && y < this.h ? this.data[y * this.w + x] : 0;
  }
  count(): number {
    let n = 0;
    for (let i = 0; i < this.data.length; i++) if (this.data[i]) n++;
    return n;
  }
}

/**
 * Fill rings (pixel coordinates, continuous; pixel (i,j) covers [i,i+1]x[j,j+1])
 * into `out` restricted to the window [wx0,wx1) x [wy0,wy1). A pixel is set when
 * its centre is inside. Calls `set(index)` for every covered pixel.
 */
export function scanFill(
  rings: Pt[][],
  evenOdd: boolean,
  w: number,
  h: number,
  set: (idx: number) => void,
  win?: [number, number, number, number]
) {
  let minY = Infinity;
  let maxY = -Infinity;
  for (const r of rings)
    for (const p of r) {
      if (p[1] < minY) minY = p[1];
      if (p[1] > maxY) maxY = p[1];
    }
  const wx0 = win ? Math.max(0, win[0]) : 0;
  const wy0 = win ? Math.max(0, win[1]) : 0;
  const wx1 = win ? Math.min(w, win[2]) : w;
  const wy1 = win ? Math.min(h, win[3]) : h;
  const j0 = Math.max(wy0, Math.ceil(minY - 0.5));
  const j1 = Math.min(wy1 - 1, Math.floor(maxY - 0.5));
  if (j0 > j1) return;
  // edge list
  const edges: { x0: number; y0: number; x1: number; y1: number; dir: number }[] = [];
  for (const r of rings) {
    for (let i = 0; i < r.length; i++) {
      const a = r[i];
      const b = r[(i + 1) % r.length];
      if (a[1] === b[1]) continue;
      if (a[1] < b[1]) edges.push({ x0: a[0], y0: a[1], x1: b[0], y1: b[1], dir: 1 });
      else edges.push({ x0: b[0], y0: b[1], x1: a[0], y1: a[1], dir: -1 });
    }
  }
  edges.sort((p, q) => p.y0 - q.y0);
  const active: typeof edges = [];
  let ei = 0;
  const xs: { x: number; d: number }[] = [];
  for (let j = j0; j <= j1; j++) {
    const yc = j + 0.5;
    while (ei < edges.length && edges[ei].y0 <= yc) active.push(edges[ei++]);
    xs.length = 0;
    for (let k = active.length - 1; k >= 0; k--) {
      const e = active[k];
      if (e.y1 <= yc) {
        active.splice(k, 1);
        continue;
      }
      if (e.y0 > yc) continue;
      xs.push({ x: e.x0 + ((yc - e.y0) * (e.x1 - e.x0)) / (e.y1 - e.y0), d: e.dir });
    }
    xs.sort((p, q) => p.x - q.x);
    let wind = 0;
    for (let k = 0; k < xs.length - 1; k++) {
      wind = evenOdd ? wind ^ 1 : wind + xs[k].d;
      if (wind === 0) continue;
      const xa = Math.max(wx0, Math.ceil(xs[k].x - 0.5));
      const xb = Math.min(wx1 - 1, Math.floor(xs[k + 1].x - 0.5));
      const row = j * w;
      for (let i = xa; i <= xb; i++) set(row + i);
    }
  }
}

export function fillPolygon(mask: Mask, rings: Pt[][], evenOdd = false, value = 1) {
  scanFill(rings, evenOdd, mask.w, mask.h, (i) => (mask.data[i] = value));
}

/** Rasterise a segment as a filled quad of the given thickness (pixels). */
export function drawLine(mask: Mask, a: Pt, b: Pt, thickness: number, value = 1) {
  const dx = b[0] - a[0];
  const dy = b[1] - a[1];
  const L = Math.hypot(dx, dy);
  const h = Math.max(thickness, 1) / 2;
  if (L < 1e-9) {
    fillPolygon(mask, [[[a[0] - h, a[1] - h], [a[0] + h, a[1] - h], [a[0] + h, a[1] + h], [a[0] - h, a[1] + h]]], false, value);
    return;
  }
  const ux = dx / L;
  const uy = dy / L;
  const nx = -uy * h;
  const ny = ux * h;
  // extend by h at both ends (square caps) so joints close
  const ax = a[0] - ux * h;
  const ay = a[1] - uy * h;
  const bx = b[0] + ux * h;
  const by = b[1] + uy * h;
  fillPolygon(
    mask,
    [[[ax + nx, ay + ny], [bx + nx, by + ny], [bx - nx, by - ny], [ax - nx, ay - ny]]],
    false,
    value
  );
}

function runMax(src: Uint8Array, dst: Uint8Array, w: number, h: number, r: number, horizontal: boolean, isMax: boolean) {
  const n = horizontal ? w : h;
  const m = horizontal ? h : w;
  const buf = new Int32Array(n + 1);
  for (let k = 0; k < m; k++) {
    // prefix sums of set pixels along the line
    buf[0] = 0;
    for (let i = 0; i < n; i++) {
      const idx = horizontal ? k * w + i : i * w + k;
      buf[i + 1] = buf[i] + (src[idx] ? 1 : 0);
    }
    for (let i = 0; i < n; i++) {
      const lo = Math.max(0, i - r);
      const hi = Math.min(n, i + r + 1);
      const s = buf[hi] - buf[lo];
      const idx = horizontal ? k * w + i : i * w + k;
      // erosion treats out-of-range as set (border does not erode)
      dst[idx] = isMax ? (s > 0 ? 1 : 0) : s === hi - lo ? 1 : 0;
    }
  }
}

export function dilate(mask: Mask, r: number): Mask {
  const tmp = new Uint8Array(mask.data.length);
  const out = new Mask(mask.w, mask.h);
  runMax(mask.data, tmp, mask.w, mask.h, r, true, true);
  runMax(tmp, out.data, mask.w, mask.h, r, false, true);
  return out;
}

export function erode(mask: Mask, r: number): Mask {
  const tmp = new Uint8Array(mask.data.length);
  const out = new Mask(mask.w, mask.h);
  runMax(mask.data, tmp, mask.w, mask.h, r, true, false);
  runMax(tmp, out.data, mask.w, mask.h, r, false, false);
  return out;
}

export const close = (m: Mask, r: number) => erode(dilate(m, r), r);
export const open = (m: Mask, r: number) => dilate(erode(m, r), r);

export interface Components {
  labels: Int32Array;
  count: number;
  area: number[];
  /** [minX, minY, maxX, maxY] per label (inclusive pixels) */
  bbox: [number, number, number, number][];
}

/** 8-connected (or 4-connected) labelling of set pixels; labels start at 1. */
export function connectedComponents(mask: Mask, eight = true): Components {
  const { w, h, data } = mask;
  const labels = new Int32Array(w * h);
  const area: number[] = [0];
  const bbox: [number, number, number, number][] = [[0, 0, 0, 0]];
  const stack = new Int32Array(w * h);
  let count = 0;
  for (let start = 0; start < data.length; start++) {
    if (!data[start] || labels[start]) continue;
    count++;
    let sp = 0;
    stack[sp++] = start;
    labels[start] = count;
    let a = 0;
    let x0 = w;
    let y0 = h;
    let x1 = -1;
    let y1 = -1;
    while (sp) {
      const idx = stack[--sp];
      a++;
      const x = idx % w;
      const y = (idx - x) / w;
      if (x < x0) x0 = x;
      if (x > x1) x1 = x;
      if (y < y0) y0 = y;
      if (y > y1) y1 = y;
      for (let dy = -1; dy <= 1; dy++) {
        const yy = y + dy;
        if (yy < 0 || yy >= h) continue;
        for (let dx = -1; dx <= 1; dx++) {
          if (!dx && !dy) continue;
          if (!eight && dx && dy) continue;
          const xx = x + dx;
          if (xx < 0 || xx >= w) continue;
          const j = yy * w + xx;
          if (data[j] && !labels[j]) {
            labels[j] = count;
            stack[sp++] = j;
          }
        }
      }
    }
    area.push(a);
    bbox.push([x0, y0, x1, y1]);
  }
  return { labels, count, area, bbox };
}

/** Set every background pixel 4-connected to the border; returns the outside mask. */
export function floodOutside(mask: Mask): Mask {
  const { w, h, data } = mask;
  const out = new Mask(w, h);
  const stack: number[] = [];
  const push = (i: number) => {
    if (!data[i] && !out.data[i]) {
      out.data[i] = 1;
      stack.push(i);
    }
  };
  for (let x = 0; x < w; x++) {
    push(x);
    push((h - 1) * w + x);
  }
  for (let y = 0; y < h; y++) {
    push(y * w);
    push(y * w + w - 1);
  }
  while (stack.length) {
    const i = stack.pop()!;
    const x = i % w;
    const y = (i - x) / w;
    if (x > 0) push(i - 1);
    if (x < w - 1) push(i + 1);
    if (y > 0) push(i - w);
    if (y < h - 1) push(i + w);
  }
  return out;
}

export interface Loop {
  label: number;
  /** Vertices on pixel corners (x right, y down), not closed */
  pts: Pt[];
  /** Signed area (positive = outer boundary, negative = hole) */
  area: number;
}

/**
 * Exact boundary loops of labelled regions along pixel edges. Diagonal contacts
 * are treated as connected (8-connectivity). Outer loops get positive area.
 * When `only` is given, only loops of those labels are traced.
 */
export function boundaryLoops(labels: Int32Array, w: number, h: number, only?: Set<number>): Loop[] {
  const L = (x: number, y: number) => (x >= 0 && y >= 0 && x < w && y < h ? labels[y * w + x] : 0);
  const W1 = w + 1;
  // directed edges: from vertex id, direction 0=right,1=down,2=left,3=up
  const out = new Map<number, number[]>(); // vertex -> packed edges (dir)
  const lab = new Map<number, number>();
  const add = (vx: number, vy: number, dir: number, label: number) => {
    const v = vy * W1 + vx;
    const key = v * 4 + dir;
    lab.set(key, label);
    const arr = out.get(v);
    if (arr) arr.push(dir);
    else out.set(v, [dir]);
  };
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const l = labels[y * w + x];
      if (!l || (only && !only.has(l))) continue;
      if (L(x, y - 1) !== l) add(x + 1, y, 2, l); // top: move left
      if (L(x, y + 1) !== l) add(x, y + 1, 0, l); // bottom: move right
      if (L(x - 1, y) !== l) add(x, y, 1, l); // left: move down
      if (L(x + 1, y) !== l) add(x + 1, y + 1, 3, l); // right: move up
    }
  }
  const DX = [1, 0, -1, 0];
  const DY = [0, 1, 0, -1];
  const loops: Loop[] = [];
  for (const [v0, dirs0] of out) {
    while (dirs0.length) {
      const label = lab.get(v0 * 4 + dirs0[0])!;
      const pts: Pt[] = [];
      let v = v0;
      let dir = dirs0[0];
      // consume
      let guard = 0;
      for (;;) {
        const arr = out.get(v)!;
        const vx = v % W1;
        const vy = (v - vx) / W1;
        pts.push([vx, vy]);
        arr.splice(arr.indexOf(dir), 1);
        const nx = vx + DX[dir];
        const ny = vy + DY[dir];
        const nv = ny * W1 + nx;
        const cand = (out.get(nv) ?? []).filter((d) => lab.get(nv * 4 + d) === label);
        if (!cand.length) break;
        let next = cand[0];
        if (cand.length > 1) {
          // right turn (screen) keeps diagonal neighbours connected
          const right = (dir + 1) % 4;
          next = cand.includes(right) ? right : cand.includes(dir) ? dir : cand[0];
        }
        v = nv;
        dir = next;
        if (++guard > 50_000_000) break;
      }
      // drop collinear vertices
      const simp: Pt[] = [];
      const n = pts.length;
      for (let i = 0; i < n; i++) {
        const p = pts[(i + n - 1) % n];
        const q = pts[i];
        const r = pts[(i + 1) % n];
        if ((q[0] - p[0]) * (r[1] - q[1]) - (q[1] - p[1]) * (r[0] - q[0]) !== 0) simp.push(q);
      }
      let a = 0;
      for (let i = 0; i < simp.length; i++) {
        const p = simp[i];
        const q = simp[(i + 1) % simp.length];
        a += p[0] * q[1] - q[0] * p[1];
      }
      // with y down and interior on the left (screen), outer loops have negative shoelace area
      loops.push({ label, pts: simp, area: -a / 2 });
    }
  }
  return loops;
}
