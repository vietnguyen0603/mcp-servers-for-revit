/**
 * Coordinate and tag helpers for the composite drafting tools (draw_detail,
 * create_drafted_table). Pure functions: no Revit access.
 */

export type DetailUnits = "mm" | "in" | "ft" | "px";

export interface Point2 {
  x: number;
  y: number;
}

export const MM_PER_INCH = 25.4;

/** 7000 px across a 42" wide sheet image. */
export const DEFAULT_PX_PER_PAPER_INCH = 7000 / 42;

const MM_PER_UNIT: Record<Exclude<DetailUnits, "px">, number> = { mm: 1, in: MM_PER_INCH, ft: 12 * MM_PER_INCH };

export interface DetailUnitOptions {
  units: DetailUnits;
  /** Input-unit point that maps to model (0,0). */
  origin?: Point2;
  /** px mode only. */
  pxPerPaperInch?: number;
  /** View scale denominator; required in px mode. */
  scale?: number;
  /** Snap converted points to multiples of this many real inches. */
  snapIn?: number;
}

export interface DetailConverter {
  units: DetailUnits;
  /** Model millimetres per input unit (px mode: per image pixel). */
  mmPerUnit: number;
  /** Input point -> model mm (px mode flips y; snapped when snapIn is set). */
  point(p: Point2): Point2;
  /** Input length (radius, fillet) -> model mm. */
  length(value: number): number;
  /** Text width: paper mm, or image px -> paper mm in px mode. */
  paperWidth(value: number): number;
}

/** Round to 1e-6 mm to keep payloads free of floating-point noise. */
function clean(value: number): number {
  const rounded = Math.round(value * 1e6) / 1e6;
  return Object.is(rounded, -0) ? 0 : rounded;
}

export function createDetailConverter(options: DetailUnitOptions): DetailConverter {
  const units = options.units;
  const origin = options.origin ?? { x: 0, y: 0 };
  let mmPerUnit: number;
  let ySign = 1;
  if (units === "px") {
    const scale = options.scale;
    if (!scale || !(scale > 0)) throw new Error("px units need the view scale (scale, createView.scale or the view's scale)");
    const pxPerPaperInch = options.pxPerPaperInch ?? DEFAULT_PX_PER_PAPER_INCH;
    if (!(pxPerPaperInch > 0)) throw new Error("pxPerPaperInch must be positive");
    mmPerUnit = (MM_PER_INCH * scale) / pxPerPaperInch;
    ySign = -1;
  } else {
    mmPerUnit = MM_PER_UNIT[units];
  }

  const snapMm = options.snapIn && options.snapIn > 0 ? options.snapIn * MM_PER_INCH : 0;
  const snap = (value: number) => (snapMm ? Math.round(value / snapMm) * snapMm : value);

  return {
    units,
    mmPerUnit,
    point: (p) => ({
      x: clean(snap((p.x - origin.x) * mmPerUnit)),
      y: clean(snap(ySign * (p.y - origin.y) * mmPerUnit)),
    }),
    length: (value) => clean(value * mmPerUnit),
    paperWidth: (value) =>
      clean(units === "px" ? (value / (options.pxPerPaperInch ?? DEFAULT_PX_PER_PAPER_INCH)) * MM_PER_INCH : value),
  };
}

/** Tag -> created element ids, in creation order. */
export type TagMap = Map<string, number[]>;

export function addTagIds(tags: TagMap, tag: string | undefined, ids: number[]): void {
  if (!tag || ids.length === 0) return;
  const existing = tags.get(tag);
  if (existing) existing.push(...ids);
  else tags.set(tag, [...ids]);
}

export interface TagReference {
  tag: string;
  /** Index into the tag's ids (poly segments); negative counts from the end. Default 0. */
  index?: number;
}

export type TagResolution = { ok: true; id: number; warning?: string } | { ok: false; message: string };

/** Resolve a `{tag, index}` dimension reference against the ids created so far. */
export function resolveTagReference(tags: TagMap, ref: TagReference): TagResolution {
  const ids = tags.get(ref.tag);
  if (!ids || ids.length === 0) {
    const known = [...tags.keys()];
    return {
      ok: false,
      message: `Unknown tag '${ref.tag}' (no element with that tag was created${
        known.length ? `; known tags: ${known.slice(0, 20).join(", ")}${known.length > 20 ? ", ..." : ""}` : ""
      })`,
    };
  }
  const requested = ref.index ?? 0;
  const position = requested < 0 ? ids.length + requested : requested;
  if (position < 0 || position >= ids.length) {
    return {
      ok: false,
      message: `Tag '${ref.tag}' has ${ids.length} element(s); index ${requested} is out of range`,
    };
  }
  const warning =
    ref.index === undefined && ids.length > 1
      ? `Tag '${ref.tag}' has ${ids.length} elements; used the first (pass index to pick another)`
      : undefined;
  return { ok: true, id: ids[position], ...(warning ? { warning } : {}) };
}

export function chunk<T>(items: T[], size: number): T[][] {
  const chunks: T[][] = [];
  for (let i = 0; i < items.length; i += size) chunks.push(items.slice(i, i + size));
  return chunks;
}

/** Text height (paper mm) parsed from a text type name like '1/8" Arial' or '3/32" ...' or '2.5mm'. */
export function textHeightFromTypeName(name: string | undefined): number | undefined {
  if (!name) return undefined;
  const fraction = name.match(/(\d+)\s*\/\s*(\d+)\s*(?:"|''|in\b)/i);
  if (fraction) {
    const value = Number(fraction[1]) / Number(fraction[2]);
    return value > 0 ? value * MM_PER_INCH : undefined;
  }
  const inches = name.match(/(\d+(?:\.\d+)?)\s*(?:"|''|in\b)/i);
  if (inches) return Number(inches[1]) * MM_PER_INCH;
  const mm = name.match(/(\d+(?:\.\d+)?)\s*mm\b/i);
  if (mm) return Number(mm[1]);
  return undefined;
}
