import { describe, expect, it } from "vitest";
import { z } from "zod";
import { dimensionElementsSchema, validateDimensionElementsArgs } from "../src/tools/dimension_elements.js";

const schema = z.object(dimensionElementsSchema).strict();

describe("dimension_elements schema", () => {
  it("accepts a typical column plan call", () => {
    expect(() =>
      schema.parse({ viewId: 1673031, categories: ["StructuralColumns", "Walls"], dimensionType: "S-DIM 2.5mm Arial", replaceExisting: true })
    ).not.toThrow();
  });
  it("rejects unknown wall parts and sides", () => {
    expect(() => schema.parse({ wallDimensions: ["height"] })).toThrow();
    expect(() => schema.parse({ side: "left" })).toThrow();
  });
  it("accepts type/family name filters and wall runs", () => {
    const args = schema.parse({
      categories: ["StructuralFoundation"],
      typeNameContains: ["PC"],
      familyNameContains: ["Pile Cap"],
      wallRuns: false,
      replaceExisting: true,
    });
    expect(validateDimensionElementsArgs(args)).toBeNull();
  });
  it("rejects empty or blank name filters", () => {
    expect(() => schema.parse({ typeNameContains: [] })).toThrow();
    expect(() => schema.parse({ familyNameContains: [""] })).toThrow();
    expect(() => schema.parse({ wallRuns: "yes" })).toThrow();
  });
  it("accepts a retype call and requires dimensionType", () => {
    const ok = schema.parse({ action: "retype", viewIds: [1, 2], dimensionType: "S-DIM 2.5mm Arial", onlyFromTypes: ["Linear - 3mm Arial"] });
    expect(validateDimensionElementsArgs(ok)).toBeNull();
    expect(validateDimensionElementsArgs(schema.parse({ action: "retype", viewIds: [1] }))).toMatch(/dimensionType/);
    expect(() => schema.parse({ action: "move" })).toThrow();
    expect(() => schema.parse({ action: "retype", viewIds: [] })).toThrow();
  });
  it("rejects mixing retype and drafting options", () => {
    expect(validateDimensionElementsArgs(schema.parse({ action: "retype", dimensionType: "X", categories: ["Walls"] }))).toMatch(/categories/);
    expect(validateDimensionElementsArgs(schema.parse({ viewIds: [5] }))).toMatch(/retype/);
    expect(validateDimensionElementsArgs(schema.parse({ action: "dimension", onlyFromTypes: ["A"] }))).toMatch(/retype/);
  });
});
