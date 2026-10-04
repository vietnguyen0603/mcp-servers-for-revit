import { z } from "zod";

/** Pixel rectangle; x1/y1 are exclusive and clamped to the image. */
export const pixelRegionSchema = z
  .object({
    x0: z.number().int().min(0),
    y0: z.number().int().min(0),
    x1: z.number().int().min(1),
    y1: z.number().int().min(1),
  })
  .refine((r) => r.x1 > r.x0 && r.y1 > r.y0, { message: "region needs x1 > x0 and y1 > y0" });

export const imagePathSchema = z.string().min(1).max(400);

/** Absolute output path ending in .png. */
export const pngSavePathSchema = z
  .string()
  .max(400)
  .regex(/^([A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+|\/)/, "savePath must be an absolute path")
  .regex(/\.png$/i, "savePath must end with .png");

export function errorResult(tool: string, error: unknown) {
  return {
    content: [
      { type: "text" as const, text: `${tool} failed: ${error instanceof Error ? error.message : String(error)}` },
    ],
    isError: true,
  };
}
