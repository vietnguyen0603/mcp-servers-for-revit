import { describe, expect, it } from "vitest";
import { z } from "zod";
import { dimensionElementsSchema } from "../src/tools/dimension_elements.js";

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
});
