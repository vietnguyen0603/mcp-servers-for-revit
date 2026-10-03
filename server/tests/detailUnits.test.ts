import { describe, expect, it } from "vitest";
import {
  addTagIds,
  chunk,
  createDetailConverter,
  DEFAULT_PX_PER_PAPER_INCH,
  resolveTagReference,
  type TagMap,
  textHeightFromTypeName,
} from "../src/utils/detailUnits.js";

describe("createDetailConverter", () => {
  it("passes millimetres through and applies origin", () => {
    const c = createDetailConverter({ units: "mm", origin: { x: 10, y: -5 } });
    expect(c.point({ x: 110, y: 95 })).toEqual({ x: 100, y: 100 });
    expect(c.length(7)).toBe(7);
    expect(c.paperWidth(40)).toBe(40);
  });

  it("converts inches and feet to model millimetres", () => {
    const inches = createDetailConverter({ units: "in" });
    expect(inches.point({ x: 12, y: -6 })).toEqual({ x: 304.8, y: -152.4 });
    expect(inches.length(0.5)).toBe(12.7);
    const feet = createDetailConverter({ units: "ft", origin: { x: 1, y: 1 } });
    expect(feet.point({ x: 3, y: 1 })).toEqual({ x: 609.6, y: 0 });
  });

  it("converts image pixels with the view scale and flips y", () => {
    // 7000 px / 42" sheet, 1" = 1'-0" (scale 12): 1000 px = 6" paper = 72" real.
    const c = createDetailConverter({ units: "px", scale: 12, origin: { x: 500, y: 2000 } });
    expect(DEFAULT_PX_PER_PAPER_INCH).toBeCloseTo(166.6667, 3);
    expect(c.point({ x: 1500, y: 2000 })).toEqual({ x: 1828.8, y: 0 });
    // y down in the image -> up in the model
    expect(c.point({ x: 500, y: 1000 })).toEqual({ x: 0, y: 1828.8 });
    expect(c.point({ x: 500, y: 2500 })).toEqual({ x: 0, y: -914.4 });
    expect(c.length(1000 / 6)).toBeCloseTo(304.8, 6);
    // text width: px -> paper mm (independent of the view scale)
    expect(c.paperWidth(DEFAULT_PX_PER_PAPER_INCH)).toBeCloseTo(25.4, 6);
  });

  it("honours a custom pxPerPaperInch", () => {
    const c = createDetailConverter({ units: "px", scale: 4, pxPerPaperInch: 100 });
    expect(c.point({ x: 100, y: 100 })).toEqual({ x: 101.6, y: -101.6 });
    expect(c.paperWidth(50)).toBe(12.7);
  });

  it("requires a scale in px mode", () => {
    expect(() => createDetailConverter({ units: "px" })).toThrow(/scale/);
  });

  it("snaps converted points to real inches", () => {
    const c = createDetailConverter({ units: "px", scale: 12, snapIn: 0.25 });
    // 1 px = 1.8288 mm real: 139 px = 254.2 mm = 10.008" -> 10"
    expect(c.point({ x: 139, y: -139 })).toEqual({ x: 254, y: 254 });
    const inches = createDetailConverter({ units: "in", snapIn: 0.25 });
    expect(inches.point({ x: 24.1, y: 0.13 })).toEqual({ x: 609.6, y: 6.35 });
    // lengths are not snapped
    expect(inches.length(0.1)).toBeCloseTo(2.54, 6);
  });
});

describe("tag resolution", () => {
  const tags: TagMap = new Map();
  addTagIds(tags, "slab", [11, 12, 13, 14]);
  addTagIds(tags, "bar", [21]);
  addTagIds(tags, "bar", [22]);
  addTagIds(tags, undefined, [99]);

  it("resolves by index, negative index and default", () => {
    expect(resolveTagReference(tags, { tag: "slab", index: 2 })).toEqual({ ok: true, id: 13 });
    expect(resolveTagReference(tags, { tag: "slab", index: -1 })).toEqual({ ok: true, id: 14 });
    const first = resolveTagReference(tags, { tag: "slab" });
    expect(first).toMatchObject({ ok: true, id: 11 });
    expect(first.ok && first.warning).toMatch(/4 elements/);
    expect(resolveTagReference(tags, { tag: "bar", index: 1 })).toEqual({ ok: true, id: 22 });
  });

  it("fails clearly for unknown tags and bad indexes", () => {
    const unknown = resolveTagReference(tags, { tag: "wall" });
    expect(unknown).toMatchObject({ ok: false });
    expect(!unknown.ok && unknown.message).toMatch(/Unknown tag 'wall'.*slab, bar/);
    const range = resolveTagReference(tags, { tag: "bar", index: 2 });
    expect(!range.ok && range.message).toMatch(/out of range/);
  });
});

describe("helpers", () => {
  it("chunks arrays", () => {
    expect(chunk([1, 2, 3, 4, 5], 2)).toEqual([[1, 2], [3, 4], [5]]);
  });

  it("parses text heights from type names", () => {
    expect(textHeightFromTypeName('1/8" Arial')).toBeCloseTo(3.175, 6);
    expect(textHeightFromTypeName('3/32" Arial-Arrow')).toBeCloseTo(2.38125, 6);
    expect(textHeightFromTypeName("2.5mm Arial")).toBe(2.5);
    expect(textHeightFromTypeName("Notes")).toBeUndefined();
  });
});
