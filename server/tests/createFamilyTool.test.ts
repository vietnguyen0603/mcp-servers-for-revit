import { describe, expect, it } from "vitest";
import { z } from "zod";
import { createFamilySchema, validateCreateFamily, type CreateFamilyArgs } from "../src/tools/create_family.js";

const schema = z.object(createFamilySchema).strict();

describe("create_family schema", () => {
  it("accepts a rectangular barrette family with types", () => {
    const args = schema.parse({
      name: "MCP_Pile-Rectangular",
      width: 1200,
      length: 2800,
      depth: 70000,
      types: [{ name: "SGBR-01 1500x2800 L78000", width: 1500, length: 2800, depth: 78000 }],
    });
    expect(validateCreateFamily(args as CreateFamilyArgs)).toBeNull();
  });

  it("accepts a circular bored pile family", () => {
    const args = schema.parse({ name: "MCP_Pile-Round", shape: "circular", diameter: 1000, types: [{ name: "D1000 L40000", diameter: 1000, depth: 40000 }] });
    expect(validateCreateFamily(args as CreateFamilyArgs)).toBeNull();
  });

  it("rejects mixing circular and rectangular sizes", () => {
    const args = schema.parse({ name: "P", shape: "circular", types: [{ name: "x", width: 1000 }] });
    expect(validateCreateFamily(args as CreateFamilyArgs)).toMatch(/circular/);
    const rect = schema.parse({ name: "P", diameter: 800 });
    expect(validateCreateFamily(rect as CreateFamilyArgs)).toMatch(/Rectangular/);
  });

  it("rejects invalid file names and non-positive sizes", () => {
    expect(() => schema.parse({ name: "a/b" })).toThrow();
    expect(() => schema.parse({ name: "P", depth: 0 })).toThrow();
  });
});

const check = (input: unknown) => validateCreateFamily(schema.parse(input) as CreateFamilyArgs);

describe("create_family column/framing sections", () => {
  it("accepts a cruciform column with an explicit second I and sized types", () => {
    expect(
      check({
        name: "MCP_Column-Cruciform",
        kind: "column",
        shape: "cruciform",
        section: { d: 1000, bf: 400, tw: 40, tf: 40, d2: 1000, bf2: 400, tw2: 40, tf2: 40 },
        types: [{ name: "C1 1000x1000x40x40", parameters: { d: 1000, d2: 1000 } }],
      })
    ).toBeNull();
  });

  it("rejects a cruciform whose flanges would touch", () => {
    expect(check({ name: "C", kind: "column", shape: "cruciform", section: { d: 1000, bf: 1000, tw: 40, tf: 40 } })).toMatch(/touch/);
  });

  it("accepts a box girder with flange overhang and renamed parameters", () => {
    expect(
      check({
        name: "MCP_Beam-Box",
        kind: "framing",
        shape: "box",
        section: { B: 500, H: 1200, tw: 16, tf: 25, overhang: 50 },
        parameterNames: { H: "Height" },
        types: [{ name: "GB1 500x1400", parameters: { Height: 1400, tf: 30 } }],
      })
    ).toBeNull();
  });

  it("validates type sizes merged with the base section", () => {
    const error = check({
      name: "B",
      kind: "framing",
      shape: "box",
      section: { B: 500, H: 1200, tw: 16, tf: 25 },
      types: [{ name: "bad", parameters: { tw: 260 } }],
    });
    expect(error).toMatch(/bad.*overhang \+ tw/);
  });

  it("requires every section key and rejects foreign keys", () => {
    expect(check({ name: "I", kind: "framing", shape: "I", section: { d: 600, bf: 300, tw: 10 } })).toMatch(/needs 'tf'/);
    expect(check({ name: "I", kind: "framing", shape: "I", section: { d: 600, bf: 300, tw: 10, tf: 16, B: 5 } })).toMatch(/does not take 'B'/);
    expect(check({ name: "I", kind: "framing", shape: "I", section: { d: 600, bf: 300, tw: 300, tf: 16 } })).toMatch(/tw/);
  });

  it("checks pipe walls and polygons", () => {
    expect(check({ name: "P", kind: "column", shape: "pipe", section: { D: 500, t: 260 } })).toMatch(/2 x t/);
    expect(check({ name: "P", kind: "column", shape: "polygon", points: [[0, 0], [1, 1], [2, 2]] })).toMatch(/no area/);
    expect(check({ name: "P", kind: "column", shape: "polygon", points: [[0, 0], [600, 0], [0, 600]] })).toBeNull();
  });

  it("forbids type parameters on fixed-geometry families", () => {
    const fixed = check({
      name: "F",
      kind: "column",
      shape: "rect",
      parametric: false,
      section: { b: 400, h: 600 },
      types: [{ name: "x", parameters: { b: 500 } }],
    });
    expect(fixed).toMatch(/fixed geometry/);
  });

  it("keeps section options and legacy fields apart", () => {
    expect(check({ name: "F", shape: "I" })).toMatch(/column/);
    expect(check({ name: "F", section: { b: 400 } })).toMatch(/only for kind/);
    expect(check({ name: "F", kind: "column", shape: "rect", section: { b: 400, h: 400 }, depth: 3000 })).toMatch(/not used/);
    expect(check({ name: "F", kind: "column", section: { b: 400, h: 400 } })).toMatch(/need shape/);
    expect(check({ name: "F", kind: "column", shape: "rectangular", section: { b: 400, h: 400 }, areaParameter: false })).toBeNull();
  });
});
