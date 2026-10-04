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
