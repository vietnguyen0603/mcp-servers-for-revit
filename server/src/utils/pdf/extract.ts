/** Orchestrates pdf_extract: load page, calibrate, pick the box, run the extractors. */
import { loadPdfPage } from "./pdfPage.js";
import {
  type CalibrationResult,
  type GridCalibrationSpec,
  type Transform,
  IDENTITY,
  findBubbles,
  fitGridCalibration,
  gridLines,
  pageSegments,
  rotationOf,
  scaleOf,
  twoPointTransform,
} from "./calibration.js";
import { type Box, Ctx } from "./context.js";
import { boxFromGrids, extractGrids, extractText, mainGridCluster } from "./grids.js";
import { extractCuts } from "./cuts.js";
import { extractBeams } from "./beams.js";
import { extractOpenings, extractSlabOutline } from "./slabs.js";
import { extractRects } from "./rects.js";
import { round, type Pt } from "./geometry.js";

export type ExtractMode = "grids" | "text" | "cuts" | "beams" | "slabOutline" | "openings" | "rects";

export interface XY {
  x: number;
  y: number;
}

export interface ExtractRequest {
  path: string;
  page: number;
  calibration?: { pointA: { pdf: XY; model: XY }; pointB: { pdf: XY; model: XY } } | { grids: GridCalibrationSpec };
  box?: { minX: number; minY: number; maxX: number; maxY: number };
  pdfRegion?: { x0: number; y0: number; x1: number; y1: number };
  extract: ExtractMode[];
  options?: {
    grids?: { clipToBox?: boolean; bubbleMinSize?: number; bubbleMaxSize?: number };
    text?: { pattern?: string };
    cuts?: {
      fillGray?: number;
      resolution?: number;
      labelPattern?: string;
      labelMaxDist?: number;
      minThickness?: number;
      maxThickness?: number;
      includePolygons?: boolean;
    };
    beams?: { labelPattern?: string; sizes?: Record<string, [number, number]>; reach?: number };
    slabOutline?: { minStrokeWidth?: number; resolution?: number };
    openings?: { minSize?: number; insideOutline?: boolean };
    rects?: { sizes: [number, number][]; tolerance?: number; minStrokeWidth?: number; labelPattern?: string; labelMaxDist?: number };
  };
}

export function compileRegex(src: string | undefined, what: string): RegExp | undefined {
  if (src === undefined) return undefined;
  try {
    return new RegExp(src);
  } catch (e) {
    throw new Error(`Invalid ${what} regex: ${(e as Error).message}`);
  }
}

const NEEDS_CALIBRATION: ExtractMode[] = ["cuts", "beams", "slabOutline", "openings", "rects"];

export async function runExtract(req: ExtractRequest): Promise<Record<string, unknown>> {
  const t0 = Date.now();
  const page = await loadPdfPage(req.path, req.page);
  const o = req.options ?? {};
  const region = req.pdfRegion ? ([req.pdfRegion.x0, req.pdfRegion.y0, req.pdfRegion.x1, req.pdfRegion.y1] as [number, number, number, number]) : undefined;

  const wantsGrids = req.extract.includes("grids") || (req.calibration && "grids" in req.calibration) || !req.box;
  let lines: ReturnType<typeof gridLines> = [];
  if (wantsGrids) {
    const segs = pageSegments(page);
    lines = gridLines(findBubbles(page, { minSize: o.grids?.bubbleMinSize, maxSize: o.grids?.bubbleMaxSize, pageRegion: region }, segs), segs);
  }

  let T: Transform = IDENTITY;
  let calibration: (CalibrationResult & { units: string }) | { units: string; note: string };
  if (!req.calibration) {
    const bad = req.extract.filter((m) => NEEDS_CALIBRATION.includes(m));
    if (bad.length) throw new Error(`Modes ${bad.join(", ")} need a calibration (explicit points or grids)`);
    calibration = { units: "pt", note: "No calibration: coordinates are PDF points, origin top-left of the page, y down" };
  } else if ("grids" in req.calibration) {
    const c = fitGridCalibration(lines, req.calibration.grids);
    T = c.transform;
    calibration = { ...c, units: "mm" };
  } else {
    const { pointA, pointB } = req.calibration;
    T = twoPointTransform([pointA.pdf.x, pointA.pdf.y], [pointA.model.x, pointA.model.y], [pointB.pdf.x, pointB.pdf.y], [pointB.model.x, pointB.model.y]);
    calibration = { transform: T, mmPerPoint: scaleOf(T), rotationDeg: rotationOf(T), method: "points", units: "mm" };
  }

  // model box: explicit, else the main grid extent (+ margin), else the whole page / region
  const pageBoxCtx = new Ctx(page, T, [-Infinity, -Infinity, Infinity, Infinity]);
  const main = mainGridCluster(lines);
  let box: Box;
  let boxSource: string;
  if (req.box) {
    box = [req.box.minX, req.box.minY, req.box.maxX, req.box.maxY];
    boxSource = "input";
  } else {
    const g = req.calibration && main.length >= 3 ? boxFromGrids(pageBoxCtx, main, 1000) : null;
    if (g) {
      box = g;
      boxSource = "grid extent";
    } else {
      const r = region ?? [0, 0, page.width, page.height];
      const a = pageBoxCtx.M([r[0], r[1]]);
      const b = pageBoxCtx.M([r[2], r[3]]);
      box = [Math.min(a[0], b[0]), Math.min(a[1], b[1]), Math.max(a[0], b[0]), Math.max(a[1], b[1])];
      boxSource = region ? "pdfRegion" : "page";
    }
  }
  const ctx = new Ctx(page, T, box);
  const result: Record<string, unknown> = {
    file: req.path,
    page: req.page,
    pageSize: { width: round(page.width, 1), height: round(page.height, 1) },
    calibration: summarizeCalibration(calibration),
    box: { minX: round(box[0]), minY: round(box[1]), maxX: round(box[2]), maxY: round(box[3]), source: boxSource },
  };
  for (const mode of req.extract) {
    switch (mode) {
      case "grids": {
        const useLines = req.box ? lines : main.length ? main : lines;
        result.grids = extractGrids(ctx, useLines, o.grids?.clipToBox ?? !!req.box);
        break;
      }
      case "text":
        result.text = extractText(ctx, compileRegex(o.text?.pattern, "text.pattern"));
        break;
      case "cuts":
        result.cuts = extractCuts(ctx, {
          fillGray: o.cuts?.fillGray,
          resolution: o.cuts?.resolution,
          labelPattern: compileRegex(o.cuts?.labelPattern, "cuts.labelPattern"),
          labelMaxDist: o.cuts?.labelMaxDist,
          minThickness: o.cuts?.minThickness,
          maxThickness: o.cuts?.maxThickness,
          includePolygons: o.cuts?.includePolygons,
        });
        break;
      case "beams":
        result.beams = extractBeams(ctx, {
          labelPattern: compileRegex(o.beams?.labelPattern, "beams.labelPattern"),
          sizes: o.beams?.sizes,
          reach: o.beams?.reach,
        });
        break;
      case "slabOutline":
        result.slabOutline = extractSlabOutline(ctx, o.slabOutline);
        break;
      case "openings": {
        let outline: Pt[] | undefined;
        if (o.openings?.insideOutline !== false) {
          const so = (result.slabOutline as ReturnType<typeof extractSlabOutline> | undefined) ?? extractSlabOutline(ctx, o.slabOutline);
          outline = so.outline.map((p) => [p.x, p.y] as Pt);
        }
        result.openings = extractOpenings(ctx, { minSize: o.openings?.minSize, outline });
        break;
      }
      case "rects":
        if (!o.rects?.sizes?.length) throw new Error("rects mode needs options.rects.sizes, e.g. [[2800,1200]]");
        result.rects = extractRects(ctx, {
          sizes: o.rects.sizes,
          tolerance: o.rects.tolerance,
          minStrokeWidth: o.rects.minStrokeWidth,
          labelPattern: compileRegex(o.rects.labelPattern, "rects.labelPattern"),
          labelMaxDist: o.rects.labelMaxDist,
        });
        break;
    }
  }
  result.elapsedMs = Date.now() - t0;
  return result;
}

function summarizeCalibration(c: any) {
  if (!c.transform) return c;
  const t = c.transform as Transform;
  return {
    method: c.method,
    units: c.units,
    mmPerPoint: round(c.mmPerPoint, 4),
    rotationDeg: round(c.rotationDeg, 4),
    transform: { a: round(t.a, 6), b: round(t.b, 6), c: round(t.c, 6), d: round(t.d, 6), e: round(t.e, 2), f: round(t.f, 2) },
    formula: "model.x = a*px + c*py + e; model.y = b*px + d*py + f (px,py = PDF points, top-left origin, y down)",
    ...(c.residuals ? { residuals: c.residuals } : {}),
  };
}

/** Counts per mode for compact summaries */
export function countsOf(result: Record<string, unknown>): Record<string, number> {
  const out: Record<string, number> = {};
  const r = result as any;
  if (r.grids) out.grids = r.grids.length;
  if (r.text) out.text = r.text.length;
  if (r.cuts) {
    out.columns = r.cuts.columns.length;
    out.walls = r.cuts.walls.length;
  }
  if (r.beams) {
    out.beams = r.beams.beams.length;
    out.beamsMissing = r.beams.missing.length;
  }
  if (r.slabOutline) out.slabOutlinePoints = r.slabOutline.outline.length;
  if (r.openings) out.openings = r.openings.openings.length;
  if (r.rects) out.rects = r.rects.rects.length;
  return out;
}
