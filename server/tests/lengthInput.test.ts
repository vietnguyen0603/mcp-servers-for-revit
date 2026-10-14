import { describe, expect, it } from "vitest";
import {
  lengthSchema,
  parseLength,
  point3LengthSchema,
  positiveLengthSchema,
  tryParseLength,
} from "../src/utils/lengthInput.js";

describe("parseLength", () => {
  it("passes numbers through unchanged as millimetres", () => {
    expect(parseLength(0)).toBe(0);
    expect(parseLength(12.5)).toBe(12.5);
    expect(parseLength(-300)).toBe(-300);
    expect(parseLength(0.1 + 0.2)).toBe(0.1 + 0.2);
    expect(() => parseLength(Number.NaN)).toThrow();
    expect(() => parseLength(Number.POSITIVE_INFINITY)).toThrow();
  });

  it.each([
    ["300", 300],
    ["300.5", 300.5],
    [".5", 0.5],
    ["300mm", 300],
    ["300 mm", 300],
    ["300 MM", 300],
    ["1.5 millimetres", 1.5],
    ["30cm", 300],
    ["0.3 m", 300],
    ["2 meters", 2000],
    ["-50 mm", -50],
  ])("parses metric/unitless %s", (input, mm) => {
    expect(parseLength(input)).toBeCloseTo(mm, 9);
  });

  it.each([
    ['1"', 25.4],
    ['3/4"', 19.05],
    ['1/2"', 12.7],
    ['0.5"', 12.7],
    ['1 1/2"', 38.1],
    ['1-1/2"', 38.1],
    ["12 in", 304.8],
    ["12in", 304.8],
    ["12 in.", 304.8],
    ["6 inches", 152.4],
    ["1 inch", 25.4],
    ["3 1/4 in", 82.55],
    ["1''", 25.4],
    ["1″", 25.4],
    ["1”", 25.4],
  ])("parses inches %s", (input, mm) => {
    expect(parseLength(input)).toBeCloseTo(mm, 9);
  });

  it.each([
    ["1'", 304.8],
    ["2'", 609.6],
    ["1.5'", 457.2],
    ["1/2'", 152.4],
    ["3 ft", 914.4],
    ["3ft", 914.4],
    ["1 foot", 304.8],
    ["2 feet", 609.6],
    ["1′", 304.8],
    ["1’", 304.8],
  ])("parses feet %s", (input, mm) => {
    expect(parseLength(input)).toBeCloseTo(mm, 9);
  });

  it.each([
    ["2'-9\"", 838.2],
    ["2' - 9\"", 838.2],
    ["2'9\"", 838.2],
    ["2' 9\"", 838.2],
    ["2'-9", 838.2],
    ["1'-0 1/2\"", 317.5],
    ["1'-0-1/2\"", 317.5],
    ["0'-3/4\"", 19.05],
    ["10'-11 3/8\"", 10 * 304.8 + 11.375 * 25.4],
    ["2 ft 6 in", 762],
    ["2ft-6in", 762],
    ["2 feet 6 inches", 762],
    ["1′-6″", 457.2],
  ])("parses feet-inches %s", (input, mm) => {
    expect(parseLength(input)).toBeCloseTo(mm, 9);
  });

  it("applies a leading sign to the whole value", () => {
    expect(parseLength("-2'-6\"")).toBeCloseTo(-762, 9);
    expect(parseLength("- 3/4\"")).toBeCloseTo(-19.05, 9);
    expect(parseLength("+1'")).toBeCloseTo(304.8, 9);
    expect(parseLength("−1\"")).toBeCloseTo(-25.4, 9);
    expect(parseLength("-0")).toBe(0);
  });

  it("uses the default unit only for unitless strings", () => {
    expect(parseLength("12", { defaultUnit: "in" })).toBeCloseTo(304.8, 9);
    expect(parseLength("1 1/2", { defaultUnit: "in" })).toBeCloseTo(38.1, 9);
    expect(parseLength("2", { defaultUnit: "ft" })).toBeCloseTo(609.6, 9);
    expect(parseLength("300mm", { defaultUnit: "in" })).toBe(300);
    expect(parseLength('3"', { defaultUnit: "ft" })).toBeCloseTo(76.2, 9);
    expect(parseLength("3/4")).toBe(0.75);
  });

  it("trims whitespace and is case-insensitive", () => {
    expect(parseLength("  2'-9\"  ")).toBeCloseTo(838.2, 9);
    expect(parseLength("12 IN")).toBeCloseTo(304.8, 9);
    expect(parseLength("3 FT 2 IN")).toBeCloseTo(965.2, 9);
  });

  it.each([
    "",
    "   ",
    "-",
    "abc",
    "12 yards",
    "1/0\"",
    "2'-",
    "-'",
    "5 - 3",
    "1..5mm",
    "1/2/3\"",
    '3"-2\'',
    "12 mm mm",
    "1e3",
    "--5",
  ])("rejects %j", (input) => {
    expect(() => parseLength(input)).toThrow();
    expect(tryParseLength(input)).toBeUndefined();
  });
});

describe("lengthSchema", () => {
  it("keeps numbers and converts strings to mm", () => {
    expect(lengthSchema.parse(42)).toBe(42);
    expect(lengthSchema.parse("2'-9\"")).toBeCloseTo(838.2, 9);
    expect(lengthSchema.parse("300mm")).toBe(300);
  });

  it("reports unparseable strings as validation errors", () => {
    const result = lengthSchema.safeParse("two feet");
    expect(result.success).toBe(false);
    expect(!result.success && result.error.issues[0].message).toMatch(/not a length/);
    expect(lengthSchema.safeParse(Number.NaN).success).toBe(false);
    expect(lengthSchema.safeParse(true).success).toBe(false);
  });

  it("positiveLengthSchema checks the converted value", () => {
    const width = positiveLengthSchema(2000);
    expect(width.parse('3"')).toBeCloseTo(76.2, 9);
    expect(width.safeParse("0").success).toBe(false);
    expect(width.safeParse("-1\"").success).toBe(false);
    expect(width.safeParse("7'").success).toBe(false);
  });

  it("point3LengthSchema converts each coordinate", () => {
    expect(point3LengthSchema.parse({ x: "1'-0\"", y: 25, z: "3/4\"" })).toEqual({ x: 304.8, y: 25, z: 19.05 });
    expect(point3LengthSchema.parse({ x: 1, y: 2 })).toEqual({ x: 1, y: 2 });
    expect(point3LengthSchema.safeParse({ x: "1 parsec", y: 0 }).success).toBe(false);
  });
});
