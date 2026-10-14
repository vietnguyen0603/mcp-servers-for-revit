import { z } from "zod";

/**
 * Length input for the drafting tools: plain numbers stay millimetres exactly
 * as before; strings may carry a unit, e.g. `2'-9"`, `3/4"`, `1'-0 1/2"`,
 * `12 in`, `2 ft 6 in`, `300mm`, `30 cm`, `0.3 m`. Pure functions, no Revit.
 */

export type LengthUnit = "mm" | "cm" | "m" | "in" | "ft";

const MM_PER: Record<LengthUnit, number> = { mm: 1, cm: 10, m: 1000, in: 25.4, ft: 304.8 };

export interface ParseLengthOptions {
  /** Unit of a string without any unit mark, e.g. "300" or "1 1/2" (default mm). */
  defaultUnit?: LengthUnit;
}

// A number: whole/decimal ("12", "1.5", ".75"), a fraction ("3/4"), or a
// mixed number ("1 1/2", "1-1/2").
const DEC = String.raw`(?:\d+(?:\.\d*)?|\.\d+)`;
const FRAC = String.raw`(?:\d+\s*\/\s*\d+)`;
const AMOUNT = String.raw`(?:\d+(?:\s+|\s*-\s*)${FRAC}|${FRAC}|${DEC})`;

const METRIC = new RegExp(String.raw`^(${DEC})\s*(mm|cm|m|millimet(?:er|re)s?|centimet(?:er|re)s?|met(?:er|re)s?)$`);
const FEET_MARK = String.raw`(?:'|ft\.?|foot|feet)`;
const INCH_MARK = String.raw`(?:"|in\.?|inch(?:es)?)`;
// [feet mark] [separator] [inches [inch mark]]
const IMPERIAL = new RegExp(
  String.raw`^(?:(${AMOUNT})\s*${FEET_MARK})?\s*(-)?\s*(?:(${AMOUNT})\s*(${INCH_MARK})?)?$`
);

/** Value of a decimal, fraction or mixed number; throws on a zero denominator. */
function amount(text: string): number {
  const t = text.trim();
  const mixed = t.match(/^(\d+)(?:\s+|\s*-\s*)(\d+)\s*\/\s*(\d+)$/);
  if (mixed) return Number(mixed[1]) + fraction(mixed[2], mixed[3]);
  const frac = t.match(/^(\d+)\s*\/\s*(\d+)$/);
  if (frac) return fraction(frac[1], frac[2]);
  return Number(t);
}

function fraction(numerator: string, denominator: string): number {
  const d = Number(denominator);
  if (d === 0) throw new Error("fraction has a zero denominator");
  return Number(numerator) / d;
}

function normalize(text: string): string {
  return text
    .trim()
    .toLowerCase()
    .replace(/[′’‘`]/g, "'") // prime / curly single quotes -> foot mark
    .replace(/[″“”]/g, '"') // double prime / curly double quotes -> inch mark
    .replace(/''/g, '"') // two single quotes -> inch mark
    .replace(/[−–—]/g, "-") // minus sign, en/em dash
    .replace(/\s+/g, " ");
}

/**
 * Parses a length to millimetres. Numbers are returned unchanged (mm).
 * Throws an Error with a readable message for unparseable input.
 */
export function parseLength(input: number | string, options: ParseLengthOptions = {}): number {
  if (typeof input === "number") {
    if (!Number.isFinite(input)) throw new Error("length must be a finite number");
    return input;
  }
  const original = input;
  let text = normalize(input);
  if (!text) throw new Error("length is empty");

  let sign = 1;
  const signMatch = text.match(/^([+-])\s*/);
  if (signMatch) {
    sign = signMatch[1] === "-" ? -1 : 1;
    text = text.slice(signMatch[0].length);
  }
  if (!text) throw new Error(`'${original}' is not a length`);

  const metric = text.match(METRIC);
  if (metric) {
    const unit = metric[2];
    const mmPerUnit = unit.startsWith("mm") || unit.startsWith("milli") ? 1 : unit.startsWith("c") ? 10 : 1000;
    return finish(sign * Number(metric[1]) * mmPerUnit, original);
  }

  const imperial = text.match(IMPERIAL);
  if (imperial) {
    const [, feet, separator, inches, inchMark] = imperial;
    if (feet === undefined && inches === undefined) throw new Error(`'${original}' is not a length`);
    if (separator && (feet === undefined || inches === undefined)) {
      throw new Error(`'${original}' is not a length (a '-' may only separate feet and inches)`);
    }
    if (feet !== undefined) {
      // After a foot mark a bare number is inches: 2'-9 == 2'-9"
      const total = amount(feet) * MM_PER.ft + (inches !== undefined ? amount(inches) * MM_PER.in : 0);
      return finish(sign * total, original);
    }
    const unit: LengthUnit = inchMark ? "in" : options.defaultUnit ?? "mm";
    return finish(sign * amount(inches as string) * MM_PER[unit], original);
  }

  throw new Error(
    `'${original}' is not a length; use mm numbers or strings like 300mm, 2'-9", 3/4", 1'-0 1/2", 12 in`
  );
}

function finish(value: number, original: string): number {
  if (!Number.isFinite(value)) throw new Error(`'${original}' is not a finite length`);
  // Trim floating-point noise (1e-9 mm) without changing exact inputs.
  const rounded = Math.round(value * 1e9) / 1e9;
  return Object.is(rounded, -0) ? 0 : rounded;
}

/** True when a length string carries a unit mark (', ", mm, in, ft, ...). */
export function hasLengthUnit(text: string): boolean {
  return /[a-z'"]/.test(normalize(text));
}

/** Like parseLength, but returns undefined instead of throwing. */
export function tryParseLength(input: number | string, options?: ParseLengthOptions): number | undefined {
  try {
    return parseLength(input, options);
  } catch {
    return undefined;
  }
}

const LENGTH_HINT = `number = mm, or a string with units: 2'-9", 3/4", 1'-0 1/2", 12 in, 300mm`;

/**
 * Zod schema for one length: a finite number (mm, unchanged) or a string with
 * units converted to mm. The parsed value is always a number in mm.
 */
export const lengthSchema = z
  .union([z.number().finite(), z.string().min(1).max(64)])
  .transform((value, ctx) => {
    if (typeof value === "number") return value;
    try {
      return parseLength(value);
    } catch (error) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, message: error instanceof Error ? error.message : String(error) });
      return z.NEVER;
    }
  })
  .describe(`Length: ${LENGTH_HINT}`);

/** A positive length (mm after parsing), optionally capped. */
export function positiveLengthSchema(maxMm?: number) {
  return lengthSchema.refine((v) => v > 0 && (maxMm === undefined || v <= maxMm), {
    message: maxMm === undefined ? "must be a positive length" : `must be a positive length up to ${maxMm} mm`,
  });
}

/** {x, y} point whose coordinates accept numbers (mm) or unit strings. */
export const point2LengthSchema = z.object({
  x: lengthSchema.describe(`X: ${LENGTH_HINT}`),
  y: lengthSchema.describe(`Y: ${LENGTH_HINT}`),
});

/** {x, y, z?} point whose coordinates accept numbers (mm) or unit strings. */
export const point3LengthSchema = point2LengthSchema.extend({
  z: lengthSchema.optional().describe(`Z (default 0): ${LENGTH_HINT}`),
});
