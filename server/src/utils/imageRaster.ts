/**
 * Pure-JS raster helpers for the image review tools (crop_image_grid,
 * overlay_images, image_info). PNG decode/encode uses `pngjs`; everything else
 * (cropping, downscaling, grid drawing, bitmap digit font, overlay
 * classification) is implemented here so no native dependency or font is needed.
 *
 * Large sheet exports (7000x5000 RGBA ~ 140 MB) are decoded once and then read
 * row by row inside the requested region; no full-size intermediate copies are
 * made. The last decoded image is cached briefly so repeated crops of the same
 * sheet do not re-decode it.
 */
import { promises as fs } from "fs";
import path from "path";
import { PNG } from "pngjs";

export interface RgbaImage {
  width: number;
  height: number;
  /** RGBA, 8 bits per channel, row-major. */
  data: Buffer;
}

export interface Region {
  x0: number;
  y0: number;
  x1: number;
  y1: number;
}

/** Integer region clamped to the image, x1/y1 exclusive. */
export interface ResolvedRegion {
  x: number;
  y: number;
  width: number;
  height: number;
}

const PNG_SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

// ---------------------------------------------------------------------------
// File access
// ---------------------------------------------------------------------------

/** Stat a file and throw a clear message when it is missing or not a file. */
export async function requireFile(filePath: string, label = "image"): Promise<{ size: number; mtimeMs: number }> {
  let stat;
  try {
    stat = await fs.stat(filePath);
  } catch {
    throw new Error(`${label} file not found: ${filePath}`);
  }
  if (!stat.isFile()) throw new Error(`${label} path is not a file: ${filePath}`);
  return { size: stat.size, mtimeMs: stat.mtimeMs };
}

export interface PngHeader {
  width: number;
  height: number;
  bitDepth: number;
  colorType: number;
}

/** Parse the PNG signature + IHDR chunk (first 33 bytes) without decoding pixels. */
export function parsePngHeader(head: Buffer, filePath = "image"): PngHeader {
  if (head.length < 33 || !head.subarray(0, 8).equals(PNG_SIGNATURE) || head.toString("ascii", 12, 16) !== "IHDR") {
    throw new Error(`${filePath} is not a PNG file (only PNG is supported; export with format PNG).`);
  }
  return {
    width: head.readUInt32BE(16),
    height: head.readUInt32BE(20),
    bitDepth: head.readUInt8(24),
    colorType: head.readUInt8(25),
  };
}

export async function readPngHeader(filePath: string): Promise<PngHeader> {
  await requireFile(filePath);
  const handle = await fs.open(filePath, "r");
  try {
    const head = Buffer.alloc(33);
    await handle.read(head, 0, 33, 0);
    return parsePngHeader(head, filePath);
  } finally {
    await handle.close();
  }
}

let cache: { key: string; image: RgbaImage; timer?: NodeJS.Timeout } | undefined;
const CACHE_TTL_MS = 120_000;

/** Decode a PNG to 8-bit RGBA. Keeps the most recent image cached for a short time. */
export async function loadPng(filePath: string, label = "image"): Promise<RgbaImage> {
  const { size, mtimeMs } = await requireFile(filePath, label);
  const key = `${path.resolve(filePath).toLowerCase()}|${size}|${mtimeMs}`;
  if (cache?.key === key) {
    refreshCacheTimer();
    return cache.image;
  }
  const buffer = await fs.readFile(filePath);
  parsePngHeader(buffer.subarray(0, 33), filePath);
  let image: RgbaImage;
  try {
    const png = PNG.sync.read(buffer);
    image = { width: png.width, height: png.height, data: png.data };
  } catch (error) {
    throw new Error(`Could not decode PNG ${filePath}: ${error instanceof Error ? error.message : String(error)}`);
  }
  if (cache?.timer) clearTimeout(cache.timer);
  cache = { key, image };
  refreshCacheTimer();
  return image;
}

function refreshCacheTimer() {
  if (!cache) return;
  if (cache.timer) clearTimeout(cache.timer);
  cache.timer = setTimeout(() => {
    cache = undefined;
  }, CACHE_TTL_MS);
  cache.timer.unref?.();
}

/** Drop the decoded-image cache (tests). */
export function clearImageCache() {
  if (cache?.timer) clearTimeout(cache.timer);
  cache = undefined;
}

/** Encode an RGBA buffer as an RGB PNG. */
export function encodePng(width: number, height: number, rgba: Buffer): Buffer {
  const png = new PNG({ width, height });
  png.data = rgba;
  return PNG.sync.write(png, { colorType: 2 });
}

export async function savePng(savePath: string, png: Buffer) {
  await fs.mkdir(path.dirname(savePath), { recursive: true });
  await fs.writeFile(savePath, png);
}

// ---------------------------------------------------------------------------
// Geometry
// ---------------------------------------------------------------------------

/** Clamp a pixel region to the image. Throws when it is empty or outside the image. */
export function resolveRegion(width: number, height: number, region?: Region): ResolvedRegion {
  if (!region) return { x: 0, y: 0, width, height };
  const x0 = Math.max(0, Math.floor(Math.min(region.x0, region.x1)));
  const y0 = Math.max(0, Math.floor(Math.min(region.y0, region.y1)));
  const x1 = Math.min(width, Math.ceil(Math.max(region.x0, region.x1)));
  const y1 = Math.min(height, Math.ceil(Math.max(region.y0, region.y1)));
  if (x1 <= x0 || y1 <= y0) {
    throw new Error(
      `region (${region.x0},${region.y0})-(${region.x1},${region.y1}) does not overlap the ${width}x${height} image`
    );
  }
  return { x: x0, y: y0, width: x1 - x0, height: y1 - y0 };
}

/** Output size and scale (<= 1) so the longer side fits maxOutputPx. */
export function fitScale(width: number, height: number, maxOutputPx: number) {
  const scale = Math.min(1, maxOutputPx / Math.max(width, height));
  return {
    scale,
    outWidth: Math.max(1, Math.round(width * scale)),
    outHeight: Math.max(1, Math.round(height * scale)),
  };
}

// ---------------------------------------------------------------------------
// Downscaling
// ---------------------------------------------------------------------------

export type ResampleMode = "ink" | "box" | "nearest";

/**
 * Fills `out` (length width*3) with the RGB values of source row `y` of the
 * region (0-based within the region).
 */
export type RowReader = (y: number, out: Uint8Array) => void;

/** Row reader over an RGBA image region, compositing alpha onto white. */
export function rgbaRegionReader(image: RgbaImage, region: ResolvedRegion): RowReader {
  const { data, width } = image;
  return (y, out) => {
    let src = ((region.y + y) * width + region.x) * 4;
    for (let x = 0, o = 0; x < region.width; x++, src += 4, o += 3) {
      const a = data[src + 3];
      if (a === 255) {
        out[o] = data[src];
        out[o + 1] = data[src + 1];
        out[o + 2] = data[src + 2];
      } else {
        const inv = 255 - a;
        out[o] = (data[src] * a + 255 * inv + 127) / 255;
        out[o + 1] = (data[src + 1] * a + 255 * inv + 127) / 255;
        out[o + 2] = (data[src + 2] * a + 255 * inv + 127) / 255;
      }
    }
  };
}

/**
 * Downscale (or copy at 1:1) a width x height RGB source read row by row into
 * an outWidth x outHeight RGBA buffer.
 *
 * - `box`: plain area average.
 * - `ink`: area average with the darkness boosted by the box side (capped at
 *   3x), so 1 px lines stay visible after reduction.
 * - `nearest`: the source pixel at the centre of each box.
 */
export function downscale(
  width: number,
  height: number,
  readRow: RowReader,
  outWidth: number,
  outHeight: number,
  mode: ResampleMode = "ink"
): Buffer {
  const out = Buffer.alloc(outWidth * outHeight * 4, 255);
  const row = new Uint8Array(width * 3);

  if (mode === "nearest" || (outWidth === width && outHeight === height)) {
    const colMap = new Int32Array(outWidth);
    for (let ox = 0; ox < outWidth; ox++) {
      colMap[ox] = Math.min(width - 1, Math.floor(((ox + 0.5) * width) / outWidth));
    }
    for (let oy = 0; oy < outHeight; oy++) {
      readRow(Math.min(height - 1, Math.floor(((oy + 0.5) * height) / outHeight)), row);
      let o = oy * outWidth * 4;
      for (let ox = 0; ox < outWidth; ox++, o += 4) {
        const s = colMap[ox] * 3;
        out[o] = row[s];
        out[o + 1] = row[s + 1];
        out[o + 2] = row[s + 2];
      }
    }
    return out;
  }

  // Source column -> output column, and per-output-column box widths.
  const colOf = new Int32Array(width);
  const colCount = new Uint32Array(outWidth);
  for (let x = 0; x < width; x++) {
    const ox = Math.min(outWidth - 1, Math.floor((x * outWidth) / width));
    colOf[x] = ox;
    colCount[ox]++;
  }
  const acc = new Float64Array(outWidth * 3);
  const boxSide = Math.sqrt((width / outWidth) * (height / outHeight));
  const gain = mode === "ink" ? Math.min(3, Math.max(1, boxSide)) : 1;

  let y = 0;
  for (let oy = 0; oy < outHeight; oy++) {
    const yEnd = oy === outHeight - 1 ? height : Math.floor(((oy + 1) * height) / outHeight);
    acc.fill(0);
    let rows = 0;
    for (; y < yEnd; y++, rows++) {
      readRow(y, row);
      for (let x = 0, s = 0; x < width; x++, s += 3) {
        const a = colOf[x] * 3;
        // accumulate darkness (255 - value)
        acc[a] += 255 - row[s];
        acc[a + 1] += 255 - row[s + 1];
        acc[a + 2] += 255 - row[s + 2];
      }
    }
    if (rows === 0) {
      // degenerate (more output rows than source rows cannot happen since scale <= 1)
      continue;
    }
    let o = oy * outWidth * 4;
    for (let ox = 0, a = 0; ox < outWidth; ox++, o += 4, a += 3) {
      const n = colCount[ox] * rows;
      if (n === 0) continue;
      out[o] = 255 - Math.min(255, Math.round((acc[a] / n) * gain));
      out[o + 1] = 255 - Math.min(255, Math.round((acc[a + 1] / n) * gain));
      out[o + 2] = 255 - Math.min(255, Math.round((acc[a + 2] / n) * gain));
    }
  }
  return out;
}

// ---------------------------------------------------------------------------
// Bitmap digit font (5x7)
// ---------------------------------------------------------------------------

const GLYPHS: Record<string, string[]> = {
  "0": ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
  "1": ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
  "2": ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
  "3": ["11111", "00010", "00100", "00010", "00001", "10001", "01110"],
  "4": ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
  "5": ["11111", "10000", "11110", "00001", "00001", "10001", "01110"],
  "6": ["00110", "01000", "10000", "11110", "10001", "10001", "01110"],
  "7": ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
  "8": ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
  "9": ["01110", "10001", "10001", "01111", "00001", "00010", "01100"],
  "-": ["00000", "00000", "00000", "11111", "00000", "00000", "00000"],
};
export const GLYPH_W = 5;
export const GLYPH_H = 7;

export function measureText(text: string, scale: number) {
  const n = text.length;
  return { width: n === 0 ? 0 : (n * GLYPH_W + (n - 1)) * scale, height: GLYPH_H * scale };
}

/** Draw digits / '-' into an RGBA buffer; pixels outside the canvas are clipped. Unknown characters render as blank. */
export function drawText(
  buf: Buffer,
  canvasWidth: number,
  canvasHeight: number,
  x: number,
  y: number,
  text: string,
  scale: number,
  color: [number, number, number]
) {
  for (let i = 0; i < text.length; i++) {
    const glyph = GLYPHS[text[i]];
    const gx = x + i * (GLYPH_W + 1) * scale;
    if (!glyph) continue;
    for (let r = 0; r < GLYPH_H; r++) {
      for (let c = 0; c < GLYPH_W; c++) {
        if (glyph[r][c] !== "1") continue;
        for (let dy = 0; dy < scale; dy++) {
          const py = y + r * scale + dy;
          if (py < 0 || py >= canvasHeight) continue;
          for (let dx = 0; dx < scale; dx++) {
            const px = gx + c * scale + dx;
            if (px < 0 || px >= canvasWidth) continue;
            const o = (py * canvasWidth + px) * 4;
            buf[o] = color[0];
            buf[o + 1] = color[1];
            buf[o + 2] = color[2];
            buf[o + 3] = 255;
          }
        }
      }
    }
  }
}

// ---------------------------------------------------------------------------
// Grid layout
// ---------------------------------------------------------------------------

export interface GridLine {
  /** Crop-local coordinate in ORIGINAL (unscaled) pixels. */
  value: number;
  /** Position on the output canvas (px, including the label margin). */
  pos: number;
  major: boolean;
  label?: { text: string; x: number; y: number };
}

export interface GridLayout {
  canvasWidth: number;
  canvasHeight: number;
  /** Where the (scaled) crop is drawn on the canvas. */
  offsetX: number;
  offsetY: number;
  outWidth: number;
  outHeight: number;
  /** Original-pixel spacing between labels actually drawn. */
  xLabelStep: number;
  yLabelStep: number;
  vertical: GridLine[];
  horizontal: GridLine[];
}

export const LABEL_SCALE = 2;
const LABEL_PAD = 3;

/**
 * Compute grid line and label positions for a crop of cropWidth x cropHeight
 * original pixels drawn at outWidth x outHeight. Lines are placed at multiples
 * of gridStep in ORIGINAL crop-local pixels (labels show those values); every
 * labelEvery-th line is major and labelled. When labels would collide after
 * downscaling, only every n-th major line is labelled.
 */
export function computeGridLayout(
  cropWidth: number,
  cropHeight: number,
  outWidth: number,
  outHeight: number,
  gridStep: number,
  labelEvery: number,
  labelOffset: { x: number; y: number } = { x: 0, y: 0 }
): GridLayout {
  const sx = outWidth / cropWidth;
  const sy = outHeight / cropHeight;
  const maxText = (n: number) => Math.max(String(Math.floor(n)).length, String(Math.floor(-n)).length);
  const xChars = Math.max(maxText(labelOffset.x), maxText(labelOffset.x + cropWidth));
  const yChars = Math.max(maxText(labelOffset.y), maxText(labelOffset.y + cropHeight));
  const xLabelW = measureText("8".repeat(xChars), LABEL_SCALE).width;
  const yLabelW = measureText("8".repeat(yChars), LABEL_SCALE).width;
  const labelH = GLYPH_H * LABEL_SCALE;

  const offsetX = yLabelW + 2 * LABEL_PAD;
  const offsetY = labelH + 2 * LABEL_PAD;
  const canvasWidth = offsetX + outWidth + Math.ceil(xLabelW / 2) + LABEL_PAD;
  const canvasHeight = offsetY + outHeight + Math.ceil(labelH / 2) + LABEL_PAD;

  const majorStep = gridStep * labelEvery;
  const xSkip = Math.max(1, Math.ceil((xLabelW + 6) / (majorStep * sx)));
  const ySkip = Math.max(1, Math.ceil((labelH + 4) / (majorStep * sy)));

  const build = (length: number, scale: number, offset: number, skip: number, vertical: boolean): GridLine[] => {
    const lines: GridLine[] = [];
    for (let k = 0; k * gridStep <= length; k++) {
      const value = k * gridStep;
      const pos = offset + Math.min(Math.round(value * scale), vertical ? outWidth - 1 : outHeight - 1);
      const major = k % labelEvery === 0;
      const line: GridLine = { value, pos, major };
      // the top-left corner is labelled once (by the x axis) so the two "0" labels do not collide
      if (major && (k / labelEvery) % skip === 0 && (vertical || k > 0)) {
        const text = String(value + (vertical ? labelOffset.x : labelOffset.y));
        const size = measureText(text, LABEL_SCALE);
        line.label = vertical
          ? { text, x: Math.max(0, Math.min(canvasWidth - size.width, pos - Math.floor(size.width / 2))), y: LABEL_PAD }
          : {
              text,
              x: offsetX - LABEL_PAD - size.width,
              y: Math.max(0, Math.min(canvasHeight - size.height, pos - Math.floor(size.height / 2))),
            };
      }
      lines.push(line);
    }
    return lines;
  };

  return {
    canvasWidth,
    canvasHeight,
    offsetX,
    offsetY,
    outWidth,
    outHeight,
    xLabelStep: majorStep * xSkip,
    yLabelStep: majorStep * ySkip,
    vertical: build(cropWidth, sx, offsetX, xSkip, true),
    horizontal: build(cropHeight, sy, offsetY, ySkip, false),
  };
}

const MINOR_COLOR: [number, number, number] = [200, 220, 245];
const MAJOR_COLOR: [number, number, number] = [120, 160, 230];
const LABEL_COLOR: [number, number, number] = [0, 60, 190];

/**
 * Compose the final canvas: white margins with labels, the scaled crop, and
 * the grid drawn underneath the drawing (per-channel darken blend, so dark
 * drawing pixels are never covered).
 */
export function renderGridCanvas(scaled: Buffer, layout: GridLayout): Buffer {
  const { canvasWidth: cw, canvasHeight: ch, offsetX, offsetY, outWidth, outHeight } = layout;
  const canvas = Buffer.alloc(cw * ch * 4, 255);
  for (let y = 0; y < outHeight; y++) {
    scaled.copy(canvas, ((offsetY + y) * cw + offsetX) * 4, y * outWidth * 4, (y + 1) * outWidth * 4);
  }
  const darken = (o: number, c: [number, number, number]) => {
    if (canvas[o] > c[0]) canvas[o] = c[0];
    if (canvas[o + 1] > c[1]) canvas[o + 1] = c[1];
    if (canvas[o + 2] > c[2]) canvas[o + 2] = c[2];
  };
  // minor lines first, then major on top
  for (const pass of [false, true]) {
    const color = pass ? MAJOR_COLOR : MINOR_COLOR;
    for (const line of layout.vertical) {
      if (line.major !== pass) continue;
      for (let y = offsetY; y < offsetY + outHeight; y++) darken((y * cw + line.pos) * 4, color);
    }
    for (const line of layout.horizontal) {
      if (line.major !== pass) continue;
      const base = line.pos * cw;
      for (let x = offsetX; x < offsetX + outWidth; x++) darken((base + x) * 4, color);
    }
  }
  for (const line of [...layout.vertical, ...layout.horizontal]) {
    if (!line.label) continue;
    drawText(canvas, cw, ch, line.label.x, line.label.y, line.label.text, LABEL_SCALE, LABEL_COLOR);
    // tick in the margin pointing at the labelled line
    if (layout.vertical.includes(line)) {
      for (let y = offsetY - LABEL_PAD; y < offsetY; y++) darken((y * cw + line.pos) * 4, LABEL_COLOR);
    } else {
      for (let x = offsetX - LABEL_PAD + 1; x < offsetX; x++) darken((line.pos * cw + x) * 4, LABEL_COLOR);
    }
  }
  return canvas;
}

// ---------------------------------------------------------------------------
// Overlay
// ---------------------------------------------------------------------------

export const CLASS_NONE = 0;
export const CLASS_MATCHED = 1;
export const CLASS_ORIGINAL_ONLY = 2;
export const CLASS_REBUILT_ONLY = 3;

/** Dark-pixel mask (1 = dark) of an image region; luminance < threshold after compositing on white. */
export function darkMask(image: RgbaImage, region: ResolvedRegion, threshold: number): Uint8Array {
  const mask = new Uint8Array(region.width * region.height);
  const row = new Uint8Array(region.width * 3);
  const read = rgbaRegionReader(image, region);
  const t = threshold * 1000;
  for (let y = 0; y < region.height; y++) {
    read(y, row);
    const base = y * region.width;
    for (let x = 0, s = 0; x < region.width; x++, s += 3) {
      if (row[s] * 299 + row[s + 1] * 587 + row[s + 2] * 114 < t) mask[base + x] = 1;
    }
  }
  return mask;
}

/** Square (Chebyshev) dilation of a 0/1 mask by radius r. */
export function dilate(mask: Uint8Array, width: number, height: number, r: number): Uint8Array {
  if (r <= 0) return mask;
  const horiz = new Uint8Array(mask.length);
  const cum = new Int32Array(width + 1);
  for (let y = 0; y < height; y++) {
    const base = y * width;
    for (let x = 0; x < width; x++) cum[x + 1] = cum[x] + mask[base + x];
    for (let x = 0; x < width; x++) {
      if (cum[Math.min(width, x + r + 1)] - cum[Math.max(0, x - r)] > 0) horiz[base + x] = 1;
    }
  }
  const out = new Uint8Array(mask.length);
  const counts = new Int32Array(width);
  for (let y = 0; y < Math.min(r, height); y++) {
    for (let x = 0; x < width; x++) counts[x] += horiz[y * width + x];
  }
  for (let y = 0; y < height; y++) {
    const add = y + r;
    if (add < height) for (let x = 0; x < width; x++) counts[x] += horiz[add * width + x];
    const remove = y - r - 1;
    if (remove >= 0) for (let x = 0; x < width; x++) counts[x] -= horiz[remove * width + x];
    for (let x = 0; x < width; x++) if (counts[x] > 0) out[y * width + x] = 1;
  }
  return out;
}

/**
 * Classify each pixel of the union of dark pixels. A dark original pixel is
 * matched when a rebuilt dark pixel lies within tolerancePx (and vice versa).
 */
export function classifyOverlay(
  original: Uint8Array,
  rebuilt: Uint8Array,
  width: number,
  height: number,
  tolerancePx = 0
): Uint8Array {
  const rebuiltNear = dilate(rebuilt, width, height, tolerancePx);
  const originalNear = dilate(original, width, height, tolerancePx);
  const classes = new Uint8Array(original.length);
  for (let i = 0; i < classes.length; i++) {
    if (original[i]) classes[i] = rebuiltNear[i] ? CLASS_MATCHED : CLASS_ORIGINAL_ONLY;
    else if (rebuilt[i]) classes[i] = originalNear[i] ? CLASS_MATCHED : CLASS_REBUILT_ONLY;
  }
  return classes;
}

export interface OverlayStats {
  darkPixels: number;
  matched: number;
  originalOnly: number;
  rebuiltOnly: number;
  matchedPct: number;
  originalOnlyPct: number;
  rebuiltOnlyPct: number;
}

const pct = (n: number, d: number) => (d === 0 ? 0 : Math.round((n / d) * 10000) / 100);

/** Count classes inside a sub-rectangle (coordinates local to the class grid, clamped). */
export function overlayStats(
  classes: Uint8Array,
  width: number,
  height: number,
  rect: ResolvedRegion = { x: 0, y: 0, width, height }
): OverlayStats {
  const counts = [0, 0, 0, 0];
  const x0 = Math.max(0, rect.x);
  const y0 = Math.max(0, rect.y);
  const x1 = Math.min(width, rect.x + rect.width);
  const y1 = Math.min(height, rect.y + rect.height);
  for (let y = y0; y < y1; y++) {
    const base = y * width;
    for (let x = x0; x < x1; x++) counts[classes[base + x]]++;
  }
  const dark = counts[1] + counts[2] + counts[3];
  return {
    darkPixels: dark,
    matched: counts[1],
    originalOnly: counts[2],
    rebuiltOnly: counts[3],
    matchedPct: pct(counts[1], dark),
    originalOnlyPct: pct(counts[2], dark),
    rebuiltOnlyPct: pct(counts[3], dark),
  };
}

const CLASS_RGB: Array<[number, number, number]> = [
  [255, 255, 255],
  [0, 0, 0],
  [230, 0, 0],
  [0, 70, 255],
];

/** Row reader that paints overlay classes: white / black / red / blue. */
export function classRowReader(classes: Uint8Array, width: number): RowReader {
  return (y, out) => {
    const base = y * width;
    for (let x = 0, o = 0; x < width; x++, o += 3) {
      const c = CLASS_RGB[classes[base + x]];
      out[o] = c[0];
      out[o + 1] = c[1];
      out[o + 2] = c[2];
    }
  };
}
