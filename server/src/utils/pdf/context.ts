/** Shared extraction context: page data + calibration + model box + helpers. */
import type { PdfPageData, PathItem } from "./pdfPage.js";
import { type Transform, type PageSeg, toModel, scaleOf, pageSegments } from "./calibration.js";
import { type Pt, dist } from "./geometry.js";
import { Mask, scanFill } from "./raster.js";

export type Box = [number, number, number, number]; // minX, minY, maxX, maxY (model)

export interface ModelText {
  text: string;
  p: Pt;
  /** Unit reading direction in model space */
  dir: Pt;
  angleDeg: number;
  /** Text height in model mm */
  size: number;
  /** Page data (for table/row logic) */
  page: { x: number; y: number; ox: number; oy: number; width: number; size: number; dir: [number, number] };
}

export class Ctx {
  readonly k: number;
  private _segs?: PageSeg[];
  private _texts?: ModelText[];
  constructor(readonly page: PdfPageData, readonly T: Transform, readonly box: Box) {
    this.k = scaleOf(T);
  }
  M(p: Pt): Pt {
    return toModel(this.T, p);
  }
  /** Map a page direction to a unit model direction */
  dirM(d: Pt): Pt {
    const x = this.T.a * d[0] + this.T.c * d[1];
    const y = this.T.b * d[0] + this.T.d * d[1];
    const l = Math.hypot(x, y) || 1;
    return [x / l, y / l];
  }
  inBox(p: Pt, margin = 0): boolean {
    const b = this.box;
    return p[0] >= b[0] - margin && p[0] <= b[2] + margin && p[1] >= b[1] - margin && p[1] <= b[3] + margin;
  }
  /** Stroked straight page segments (cached) */
  pageSegs(): PageSeg[] {
    this._segs ??= pageSegments(this.page);
    return this._segs;
  }
  /** Model-space segments, optionally filtered by stroke width and box. */
  modelSegs(opts: { minWidth?: number; maxWidth?: number; inBoxMargin?: number } = {}) {
    const out: { a: Pt; b: Pt; w: number }[] = [];
    for (const s of this.pageSegs()) {
      if (opts.minWidth !== undefined && s.w < opts.minWidth) continue;
      if (opts.maxWidth !== undefined && s.w > opts.maxWidth) continue;
      const a = this.M(s.a);
      const b = this.M(s.b);
      if (opts.inBoxMargin !== undefined && !this.inBox(a, opts.inBoxMargin) && !this.inBox(b, opts.inBoxMargin)) continue;
      out.push({ a, b, w: s.w });
    }
    return out;
  }
  texts(): ModelText[] {
    if (!this._texts) {
      this._texts = this.page.texts.map((t) => {
        const dir = this.dirM(t.dir);
        return {
          text: t.str,
          p: this.M([t.x, t.y]),
          dir,
          angleDeg: (Math.atan2(dir[1], dir[0]) * 180) / Math.PI,
          size: t.size * this.k,
          page: { x: t.x, y: t.y, ox: t.ox, oy: t.oy, width: t.width, size: t.size, dir: t.dir },
        };
      });
    }
    return this._texts;
  }
  labels(re: RegExp, margin = 0): ModelText[] {
    return this.texts().filter((t) => re.test(t.text) && this.inBox(t.p, margin));
  }
}

export function nearestLabel(labels: ModelText[], p: Pt, maxDist: number): string | null {
  let best: ModelText | null = null;
  let bd = Infinity;
  for (const l of labels) {
    const d = dist(l.p, p);
    if (d < bd) {
      bd = d;
      best = l;
    }
  }
  return best && bd <= maxDist ? best.text : null;
}

/** Raster aligned with the model box: pixel (i,j) <-> model (x0 + i*res, y1 - j*res). */
export class ModelRaster {
  readonly mask: Mask;
  constructor(readonly box: Box, readonly res: number) {
    const w = Math.max(1, Math.ceil((box[2] - box[0]) / res));
    const h = Math.max(1, Math.ceil((box[3] - box[1]) / res));
    this.mask = new Mask(w, h);
  }
  toPx(p: Pt): Pt {
    return [(p[0] - this.box[0]) / this.res, (this.box[3] - p[1]) / this.res];
  }
  toModel(px: Pt): Pt {
    return [this.box[0] + px[0] * this.res, this.box[3] - px[1] * this.res];
  }
  /** Is the model point inside a set pixel (3x3 neighbourhood)? */
  near(p: Pt, m: Mask = this.mask): boolean {
    const ix = Math.floor((p[0] - this.box[0]) / this.res);
    const iy = Math.floor((this.box[3] - p[1]) / this.res);
    for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) if (m.get(ix + dx, iy + dy)) return true;
    return false;
  }
}

/** Pick a raster resolution (mm/px) that keeps the raster under maxPixels. */
export function autoRes(box: Box, preferred: number, maxPixels = 16e6): number {
  const area = (box[2] - box[0]) * (box[3] - box[1]);
  return Math.max(preferred, Math.sqrt(area / maxPixels));
}

/**
 * Fill a painted page path into the model raster, honouring its clip regions.
 */
export function rasterizeFill(r: ModelRaster, ctx: Ctx, p: PathItem) {
  const toPx = (pts: number[]): Pt[] => {
    const out: Pt[] = [];
    for (let i = 0; i < pts.length; i += 2) out.push(r.toPx(ctx.M([pts[i], pts[i + 1]])));
    return out;
  };
  const rings = p.subpaths.map((s) => toPx(s.pts));
  const bb = ringsBBox(rings);
  let win: [number, number, number, number] = [
    Math.max(0, Math.floor(bb[0])),
    Math.max(0, Math.floor(bb[1])),
    Math.min(r.mask.w, Math.ceil(bb[2]) + 1),
    Math.min(r.mask.h, Math.ceil(bb[3]) + 1),
  ];
  const clips: { rings: Pt[][]; evenOdd: boolean }[] = [];
  for (const c of p.clips) {
    const cr = c.rings.map(toPx);
    const cb = ringsBBox(cr);
    // an axis-aligned rectangle clip that contains the window needs no work
    if (cr.length === 1 && isAxisRect(cr[0]) && cb[0] <= win[0] && cb[1] <= win[1] && cb[2] >= win[2] && cb[3] >= win[3]) continue;
    win = [Math.max(win[0], Math.floor(cb[0])), Math.max(win[1], Math.floor(cb[1])), Math.min(win[2], Math.ceil(cb[2]) + 1), Math.min(win[3], Math.ceil(cb[3]) + 1)];
    clips.push({ rings: cr, evenOdd: c.evenOdd });
  }
  if (win[0] >= win[2] || win[1] >= win[3]) return;
  const W = r.mask.w;
  if (!clips.length) {
    scanFill(rings, p.evenOdd, W, r.mask.h, (i) => (r.mask.data[i] = 1), win);
    return;
  }
  const ww = win[2] - win[0];
  const local = new Uint8Array(ww * (win[3] - win[1]));
  const li = (i: number) => {
    const x = i % W;
    const y = (i - x) / W;
    return (y - win[1]) * ww + (x - win[0]);
  };
  scanFill(rings, p.evenOdd, W, r.mask.h, (i) => (local[li(i)] = 1), win);
  let level = 1;
  for (const c of clips) {
    const next = level + 1;
    scanFill(c.rings, c.evenOdd, W, r.mask.h, (i) => {
      const j = li(i);
      if (local[j] === level) local[j] = next;
    }, win);
    level = next;
  }
  for (let j = 0; j < local.length; j++) {
    if (local[j] === level) {
      const y = Math.floor(j / ww) + win[1];
      const x = (j % ww) + win[0];
      r.mask.data[y * W + x] = 1;
    }
  }
}

function ringsBBox(rings: Pt[][]): [number, number, number, number] {
  let x0 = Infinity;
  let y0 = Infinity;
  let x1 = -Infinity;
  let y1 = -Infinity;
  for (const r of rings)
    for (const p of r) {
      x0 = Math.min(x0, p[0]);
      y0 = Math.min(y0, p[1]);
      x1 = Math.max(x1, p[0]);
      y1 = Math.max(y1, p[1]);
    }
  return [x0, y0, x1, y1];
}

function isAxisRect(r: Pt[]): boolean {
  const pts = r.length === 5 ? r.slice(0, 4) : r;
  if (pts.length !== 4) return false;
  for (let i = 0; i < 4; i++) {
    const a = pts[i];
    const b = pts[(i + 1) % 4];
    if (Math.abs(a[0] - b[0]) > 1e-6 && Math.abs(a[1] - b[1]) > 1e-6) return false;
  }
  return true;
}

/** Spatial hash of points for nearest-point snapping. */
export class PointIndex {
  private cells = new Map<string, Pt[]>();
  constructor(points: Pt[], private cell: number) {
    for (const p of points) {
      const k = this.key(p);
      const a = this.cells.get(k);
      if (a) a.push(p);
      else this.cells.set(k, [p]);
    }
  }
  private key(p: Pt, dx = 0, dy = 0) {
    return `${Math.floor(p[0] / this.cell) + dx}|${Math.floor(p[1] / this.cell) + dy}`;
  }
  nearest(p: Pt, maxDist: number): Pt | null {
    let best: Pt | null = null;
    let bd = maxDist;
    const r = Math.ceil(maxDist / this.cell);
    for (let dx = -r; dx <= r; dx++)
      for (let dy = -r; dy <= r; dy++) {
        const a = this.cells.get(this.key(p, dx, dy));
        if (!a) continue;
        for (const q of a) {
          const d = dist(p, q);
          if (d < bd) {
            bd = d;
            best = q;
          }
        }
      }
    return best;
  }
}
