import { describe, expect, it } from "vitest";
import { z } from "zod";
import { snapToGridShape } from "../src/tools/snap_to_grid.js";
import { styleTagFamiliesShape } from "../src/tools/style_tag_families.js";
import { tagElementsShape } from "../src/tools/tag_elements.js";

const snap = z.object(snapToGridShape).strict();
const style = z.object(styleTagFamiliesShape).strict();
const tag = z.object(tagElementsShape).strict();

describe("snap_to_grid schema", () => {
  it("defaults to a 50 mm grid-relative dry run", () => {
    const parsed = snap.parse({ categories: ["StructuralColumns"] });
    expect(parsed).toMatchObject({ axes: "grid", stepMm: 50, maxShiftMm: 30, maxGridDistanceMm: 5000, dryRun: true });
  });

  it("accepts finer steps, levels, type filter and global axes", () => {
    expect(() =>
      snap.parse({
        categories: ["StructuralFoundation", "Walls", "StructuralFraming"],
        levels: ["L1", 3000],
        typeNameContains: "C1",
        stepMm: 5,
        maxShiftMm: 10,
        axes: "global",
        globalOrigin: "internal",
        dryRun: false,
      })
    ).not.toThrow();
    expect(snap.parse({ elementIds: [1, 2], stepMm: 25 }).stepMm).toBe(25);
  });

  it("rejects invalid values", () => {
    expect(() => snap.parse({ axes: "local" })).toThrow();
    expect(() => snap.parse({ stepMm: 0 })).toThrow();
    expect(() => snap.parse({ maxShiftMm: -1 })).toThrow();
    expect(() => snap.parse({ globalOrigin: "survey" })).toThrow();
  });
});

describe("style_tag_families schema", () => {
  it("accepts category selection with a project text type", () => {
    const parsed = style.parse({
      categories: ["OST_StructuralFramingTags", "OST_StructuralColumnTags", "OST_GridHeads"],
      likeTextType: "S-TEXT 2.5mm Arial",
      lineWeight: 1,
    });
    expect(parsed).toMatchObject({ dryRun: false, familyCategoryLines: true, projectObjectStyles: true, maxFamilies: 100 });
  });

  it("accepts explicit text values and colours", () => {
    expect(() =>
      style.parse({
        families: ["M_Structural Framing Tag"],
        font: "Arial",
        textSizeMm: 2.5,
        widthFactor: 0.8,
        bold: false,
        color: "#000000",
        lineColor: { r: 0, g: 0, b: 0 },
        background: "transparent",
        dryRun: true,
      })
    ).not.toThrow();
  });

  it("rejects bad pens, colours and backgrounds", () => {
    expect(() => style.parse({ families: ["A"], lineWeight: 17 })).toThrow();
    expect(() => style.parse({ families: ["A"], color: "#12345" })).toThrow();
    expect(() => style.parse({ families: ["A"], background: "white" })).toThrow();
    expect(() => style.parse({ families: ["A"], textSizeMm: 0 })).toThrow();
  });
});

describe("tag_elements placement presets", () => {
  it("keeps the old defaults when no preset is given", () => {
    const parsed = tag.parse({ categories: ["OST_StructuralColumns"] });
    expect(parsed.placement).toBeUndefined();
    expect(parsed.leader).toBeUndefined();
    expect(parsed).toMatchObject({ orientation: "Horizontal", addLeader: false, untaggedOnly: true, avoidOverlaps: false });
  });

  it("accepts presets, paper offsets, leader modes and tag type names", () => {
    for (const placement of ["center", "above", "below", "left", "right", "topRight", "topLeft", "bottomRight", "bottomLeft"]) {
      expect(() => tag.parse({ categories: ["OST_StructuralColumns"], placement })).not.toThrow();
    }
    expect(() =>
      tag.parse({
        categories: ["OST_StructuralFraming"],
        placement: "above",
        orientation: "Model",
        offsetPaperMm: 2,
        leader: "auto",
        leaderThresholdPaperMm: 4,
        avoidOverlaps: true,
        tagTypeName: "M_Structural Framing Tag: Standard",
      })
    ).not.toThrow();
  });

  it("rejects unknown presets and leader modes", () => {
    expect(() => tag.parse({ categories: ["X"], placement: "middle" })).toThrow();
    expect(() => tag.parse({ categories: ["X"], leader: "sometimes" })).toThrow();
    expect(() => tag.parse({ categories: ["X"], offsetPaperMm: -1 })).toThrow();
  });
});

describe("tag_elements replaceExisting / maxShiftPaperMm", () => {
  const schema = z.object(tagElementsShape);
  it("accepts the re-run and shift-cap options", () => {
    expect(schema.safeParse({ categories: ["OST_StructuralFraming"], replaceExisting: true, avoidOverlaps: true, maxShiftPaperMm: 12 }).success).toBe(true);
  });
  it("rejects a non-positive shift cap", () => {
    expect(schema.safeParse({ maxShiftPaperMm: 0 }).success).toBe(false);
  });
});
