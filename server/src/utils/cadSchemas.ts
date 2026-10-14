import { z } from "zod";

/** Shared input schemas for the CAD link / read / trace / purge tools. */

export const layerPatternsSchema = z
  .array(z.string().min(1).max(256))
  .min(1)
  .max(500)
  .describe("Layer names; * and ? wildcards allowed, case-insensitive");

export const cadRegionSchema = z
  .object({
    minX: z.number().finite(),
    minY: z.number().finite(),
    maxX: z.number().finite(),
    maxY: z.number().finite(),
  })
  .strict()
  .refine((r) => r.maxX > r.minX && r.maxY > r.minY, { message: "region needs maxX > minX and maxY > minY" })
  .describe("Plan rectangle in model mm (X/Y) to keep items from");

export const regionModeSchema = z
  .enum(["intersects", "inside"])
  .optional()
  .default("intersects")
  .describe("intersects: keep items touching the region (default); inside: only items fully inside");

export function cadError(tool: string, message: string) {
  return { content: [{ type: "text" as const, text: `${tool}: ${message}` }], isError: true };
}
