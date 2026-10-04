/**
 * Read one PDF page into plain geometry: text items and painted paths in page
 * coordinates (PDF points, origin at the top-left of the displayed page, y down).
 * Pure JS on top of pdfjs-dist; no canvas or native modules.
 */
import fs from "fs/promises";

export type Mat = [number, number, number, number, number, number];
export type Rgb = [number, number, number];

export interface Subpath {
  /** Flattened points x0,y0,x1,y1,... in page coordinates */
  pts: number[];
  closed: boolean;
  /** Number of bezier pieces in the original subpath */
  curves: number;
  /** Number of straight pieces in the original subpath */
  lines: number;
}

export interface ClipRegion {
  rings: number[][];
  evenOdd: boolean;
  bbox: [number, number, number, number];
}

export interface PathItem {
  fill: boolean;
  stroke: boolean;
  evenOdd: boolean;
  /** Stroke width in page units (line width scaled by the CTM) */
  lineWidth: number;
  fillColor: Rgb | null;
  strokeColor: Rgb | null;
  dashed: boolean;
  subpaths: Subpath[];
  /** Active clip regions (intersection), page-level clips included */
  clips: ClipRegion[];
  bbox: [number, number, number, number];
}

export interface TextItem {
  str: string;
  /** Visual centre of the text in page coordinates */
  x: number;
  y: number;
  /** Baseline origin */
  ox: number;
  oy: number;
  /** Unit reading direction in page coordinates (y down) */
  dir: [number, number];
  /** Font size in page units */
  size: number;
  /** Advance length along dir in page units */
  width: number;
}

export interface PdfPageData {
  width: number;
  height: number;
  texts: TextItem[];
  paths: PathItem[];
}

type PdfJs = typeof import("pdfjs-dist/legacy/build/pdf.mjs");
let pdfjsPromise: Promise<PdfJs> | null = null;
function loadPdfJs(): Promise<PdfJs> {
  if (!pdfjsPromise) {
    installDomStubs();
    pdfjsPromise = import("pdfjs-dist/legacy/build/pdf.mjs");
  }
  return pdfjsPromise;
}

/**
 * pdfjs' display layer constructs a DOMMatrix at module load and otherwise
 * polyfills DOMMatrix/Path2D/ImageData from the optional native @napi-rs/canvas.
 * We never render, so inert pure-JS stubs are enough and keep the tool free of
 * native code (works with `npm install --omit=optional`).
 */
function installDomStubs() {
  const g = globalThis as Record<string, unknown>;
  if (!g.DOMMatrix) {
    g.DOMMatrix = class DOMMatrixStub {
      a = 1;
      b = 0;
      c = 0;
      d = 1;
      e = 0;
      f = 0;
      constructor(init?: ArrayLike<number>) {
        if (init && init.length >= 6) [this.a, this.b, this.c, this.d, this.e, this.f] = Array.from(init).slice(0, 6);
      }
      multiplySelf() {
        return this;
      }
      preMultiplySelf() {
        return this;
      }
      invertSelf() {
        return this;
      }
      translate() {
        return this;
      }
      scale() {
        return this;
      }
    };
  }
  if (!g.Path2D) {
    g.Path2D = class Path2DStub {
      addPath() {}
      moveTo() {}
      lineTo() {}
      bezierCurveTo() {}
      quadraticCurveTo() {}
      closePath() {}
      rect() {}
    };
  }
  if (!g.ImageData) {
    g.ImageData = class ImageDataStub {
      constructor(
        readonly data: unknown,
        readonly width: number,
        readonly height: number
      ) {}
    };
  }
}

export function mul(m1: Mat, m2: Mat): Mat {
  return [
    m1[0] * m2[0] + m1[2] * m2[1],
    m1[1] * m2[0] + m1[3] * m2[1],
    m1[0] * m2[2] + m1[2] * m2[3],
    m1[1] * m2[2] + m1[3] * m2[3],
    m1[0] * m2[4] + m1[2] * m2[5] + m1[4],
    m1[1] * m2[4] + m1[3] * m2[5] + m1[5],
  ];
}

export function apply(m: Mat, x: number, y: number): [number, number] {
  return [x * m[0] + y * m[2] + m[4], x * m[1] + y * m[3] + m[5]];
}

function parseColor(arg: unknown): Rgb | null {
  if (typeof arg === "string" && /^#[0-9a-f]{6}$/i.test(arg)) {
    return [parseInt(arg.slice(1, 3), 16), parseInt(arg.slice(3, 5), 16), parseInt(arg.slice(5, 7), 16)];
  }
  if (Array.isArray(arg) && arg.length >= 3 && arg.every((v) => typeof v === "number")) {
    return [arg[0], arg[1], arg[2]];
  }
  return null;
}

const DRAW_MOVE = 0;
const DRAW_LINE = 1;
const DRAW_CURVE = 2;
const DRAW_QUAD = 3;
const DRAW_CLOSE = 4;

/** Flatten pdfjs DrawOPS path data into subpaths (already transformed by m). */
export function flattenPath(data: ArrayLike<number>, m: Mat, curveSteps = 8): Subpath[] {
  const out: Subpath[] = [];
  let cur = null as Subpath | null;
  let cx = 0;
  let cy = 0;
  let sx = 0;
  let sy = 0;
  const push = (x: number, y: number) => {
    const [px, py] = apply(m, x, y);
    cur!.pts.push(px, py);
  };
  const start = (x: number, y: number) => {
    if (cur && cur.pts.length >= 4) out.push(cur);
    cur = { pts: [], closed: false, curves: 0, lines: 0 };
    push(x, y);
    cx = sx = x;
    cy = sy = y;
  };
  let i = 0;
  while (i < data.length) {
    const op = data[i++];
    if (op === DRAW_MOVE) {
      start(data[i], data[i + 1]);
      i += 2;
    } else if (op === DRAW_LINE) {
      if (!cur) start(cx, cy);
      push(data[i], data[i + 1]);
      cur!.lines++;
      cx = data[i];
      cy = data[i + 1];
      i += 2;
    } else if (op === DRAW_CURVE || op === DRAW_QUAD) {
      if (!cur) start(cx, cy);
      let x1: number, y1: number, x2: number, y2: number, x3: number, y3: number;
      if (op === DRAW_CURVE) {
        [x1, y1, x2, y2, x3, y3] = [data[i], data[i + 1], data[i + 2], data[i + 3], data[i + 4], data[i + 5]];
        i += 6;
      } else {
        const [qx, qy, ex, ey] = [data[i], data[i + 1], data[i + 2], data[i + 3]];
        x1 = cx + (2 / 3) * (qx - cx);
        y1 = cy + (2 / 3) * (qy - cy);
        x2 = ex + (2 / 3) * (qx - ex);
        y2 = ey + (2 / 3) * (qy - ey);
        x3 = ex;
        y3 = ey;
        i += 4;
      }
      for (let s = 1; s <= curveSteps; s++) {
        const t = s / curveSteps;
        const u = 1 - t;
        const x = u * u * u * cx + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x3;
        const y = u * u * u * cy + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3;
        push(x, y);
      }
      cur!.curves++;
      cx = x3;
      cy = y3;
    } else if (op === DRAW_CLOSE) {
      if (cur) {
        cur.closed = true;
        const n = cur.pts.length;
        const [px, py] = apply(m, sx, sy);
        if (Math.abs(cur.pts[n - 2] - px) > 1e-6 || Math.abs(cur.pts[n - 1] - py) > 1e-6) {
          cur.pts.push(px, py);
          cur.lines++;
        }
        out.push(cur);
        cur = null;
      }
      cx = sx;
      cy = sy;
    } else {
      break; // unknown op: stop rather than misread the buffer
    }
  }
  if (cur && (cur as Subpath).pts.length >= 4) out.push(cur);
  return out;
}

function bboxOf(subpaths: Subpath[]): [number, number, number, number] {
  let x0 = Infinity;
  let y0 = Infinity;
  let x1 = -Infinity;
  let y1 = -Infinity;
  for (const s of subpaths) {
    for (let i = 0; i < s.pts.length; i += 2) {
      const x = s.pts[i];
      const y = s.pts[i + 1];
      if (x < x0) x0 = x;
      if (x > x1) x1 = x;
      if (y < y0) y0 = y;
      if (y > y1) y1 = y;
    }
  }
  return [x0, y0, x1, y1];
}

interface GState {
  ctm: Mat;
  lineWidth: number;
  fillColor: Rgb | null;
  strokeColor: Rgb | null;
  dashed: boolean;
  clips: ClipRegion[];
}

/** Interpret a pdfjs operator list into painted paths. Exported for tests. */
export function interpretOperatorList(
  fnArray: number[],
  argsArray: unknown[],
  OPS: Record<string, number>,
  baseTransform: Mat
): PathItem[] {
  const paths: PathItem[] = [];
  let st: GState = { ctm: baseTransform, lineWidth: 1, fillColor: [0, 0, 0], strokeColor: [0, 0, 0], dashed: false, clips: [] };
  const stack: GState[] = [];
  let pendingClip: "nonzero" | "evenodd" | null = null;
  const paint = new Map<number, { fill: boolean; stroke: boolean; evenOdd: boolean }>([
    [OPS.stroke, { fill: false, stroke: true, evenOdd: false }],
    [OPS.closeStroke, { fill: false, stroke: true, evenOdd: false }],
    [OPS.fill, { fill: true, stroke: false, evenOdd: false }],
    [OPS.eoFill, { fill: true, stroke: false, evenOdd: true }],
    [OPS.fillStroke, { fill: true, stroke: true, evenOdd: false }],
    [OPS.eoFillStroke, { fill: true, stroke: true, evenOdd: true }],
    [OPS.closeFillStroke, { fill: true, stroke: true, evenOdd: false }],
    [OPS.closeEOFillStroke, { fill: true, stroke: true, evenOdd: true }],
    [OPS.endPath, { fill: false, stroke: false, evenOdd: false }],
  ]);
  const scaleOf = (m: Mat) => Math.sqrt(Math.abs(m[0] * m[3] - m[1] * m[2]));

  for (let k = 0; k < fnArray.length; k++) {
    const fn = fnArray[k];
    const args = argsArray[k] as any;
    switch (fn) {
      case OPS.save:
        stack.push(st);
        st = { ...st };
        break;
      case OPS.restore:
        if (stack.length) st = stack.pop()!;
        break;
      case OPS.transform:
        st.ctm = mul(st.ctm, args as Mat);
        break;
      case OPS.paintFormXObjectBegin: {
        stack.push(st);
        st = { ...st };
        if (Array.isArray(args?.[0]) || ArrayBuffer.isView(args?.[0])) st.ctm = mul(st.ctm, Array.from(args[0] as number[]) as Mat);
        const bb = args?.[1];
        if (bb && bb.length === 4) {
          const rect = [bb[0], bb[1], bb[2], bb[1], bb[2], bb[3], bb[0], bb[3]];
          const ring: number[] = [];
          for (let i = 0; i < 8; i += 2) ring.push(...apply(st.ctm, rect[i], rect[i + 1]));
          st.clips = [...st.clips, { rings: [ring], evenOdd: false, bbox: bboxOf([{ pts: ring, closed: true, curves: 0, lines: 4 }]) }];
        }
        break;
      }
      case OPS.paintFormXObjectEnd:
        if (stack.length) st = stack.pop()!;
        break;
      case OPS.setLineWidth:
        st.lineWidth = args[0];
        break;
      case OPS.setDash: {
        const arr = args?.[0];
        st.dashed = Array.isArray(arr) && arr.length > 0 && arr.some((v: number) => v > 0);
        break;
      }
      case OPS.setFillRGBColor:
        st.fillColor = parseColor(args?.[0]);
        break;
      case OPS.setStrokeRGBColor:
        st.strokeColor = parseColor(args?.[0]);
        break;
      case OPS.setFillColorN:
      case OPS.shadingFill:
        if (fn === OPS.setFillColorN) st.fillColor = null;
        break;
      case OPS.setStrokeColorN:
        st.strokeColor = null;
        break;
      case OPS.clip:
        pendingClip = "nonzero";
        break;
      case OPS.eoClip:
        pendingClip = "evenodd";
        break;
      case OPS.constructPath: {
        const mode = paint.get(args[0]);
        const data = args[1]?.[0];
        if (!mode || !data) {
          pendingClip = null;
          break;
        }
        const subpaths = flattenPath(data, st.ctm);
        if (!subpaths.length) {
          pendingClip = null;
          break;
        }
        const bbox = bboxOf(subpaths);
        if (mode.fill || mode.stroke) {
          paths.push({
            fill: mode.fill,
            stroke: mode.stroke,
            evenOdd: mode.evenOdd,
            lineWidth: st.lineWidth * scaleOf(st.ctm),
            fillColor: st.fillColor,
            strokeColor: st.strokeColor,
            dashed: st.dashed,
            subpaths,
            clips: st.clips,
            bbox,
          });
        }
        if (pendingClip) {
          st.clips = [...st.clips, { rings: subpaths.map((s) => s.pts), evenOdd: pendingClip === "evenodd", bbox }];
          pendingClip = null;
        }
        break;
      }
      default:
        break;
    }
  }
  return paths;
}

export function textItemsFrom(items: any[], viewportTransform: Mat): TextItem[] {
  const out: TextItem[] = [];
  for (const it of items) {
    if (typeof it?.str !== "string") continue;
    const str = it.str.trim();
    if (!str) continue;
    const m = mul(viewportTransform, it.transform as Mat);
    const ax = m[0];
    const ay = m[1];
    const len = Math.hypot(ax, ay) || 1;
    const dir: [number, number] = [ax / len, ay / len];
    // "up" in page coords (y down): rotate dir by -90 deg on screen
    const up: [number, number] = [dir[1], -dir[0]];
    const size = Math.hypot(m[2], m[3]) || it.height || 0;
    const width = it.width || 0;
    const ox = m[4];
    const oy = m[5];
    out.push({
      str,
      ox,
      oy,
      x: ox + (dir[0] * width) / 2 + up[0] * size * 0.35,
      y: oy + (dir[1] * width) / 2 + up[1] * size * 0.35,
      dir,
      size,
      width,
    });
  }
  return out;
}

const cache = new Map<string, Promise<PdfPageData>>();

/** Load and interpret a page (1-based). Results are cached per file+mtime+page. */
export async function loadPdfPage(filePath: string, pageNumber: number): Promise<PdfPageData> {
  const stat = await fs.stat(filePath);
  const key = `${filePath}|${stat.mtimeMs}|${pageNumber}`;
  let p = cache.get(key);
  if (!p) {
    p = readPage(filePath, pageNumber);
    cache.set(key, p);
    p.catch(() => cache.delete(key));
    if (cache.size > 8) cache.delete(cache.keys().next().value!);
  }
  return p;
}

async function readPage(filePath: string, pageNumber: number): Promise<PdfPageData> {
  const pdfjs = await loadPdfJs();
  const data = new Uint8Array(await fs.readFile(filePath));
  const doc = await pdfjs.getDocument({ data, verbosity: 0, isEvalSupported: false, useSystemFonts: false }).promise;
  try {
    if (pageNumber < 1 || pageNumber > doc.numPages) {
      throw new Error(`Page ${pageNumber} is out of range (the PDF has ${doc.numPages} pages)`);
    }
    const page = await doc.getPage(pageNumber);
    const viewport = page.getViewport({ scale: 1 });
    const vt = viewport.transform as Mat;
    const ol = await page.getOperatorList();
    const paths = interpretOperatorList(ol.fnArray, ol.argsArray, pdfjs.OPS as unknown as Record<string, number>, vt);
    const tc = await page.getTextContent();
    const texts = textItemsFrom(tc.items as any[], vt);
    return { width: viewport.width, height: viewport.height, texts, paths };
  } finally {
    await doc.destroy();
  }
}
