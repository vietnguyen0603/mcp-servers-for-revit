import { z } from "zod";

/** Graphic override object shared by override_graphics and create_view_filter. */

const rgbSchema = z
  .tuple([z.number().int().min(0).max(255), z.number().int().min(0).max(255), z.number().int().min(0).max(255)])
  .describe("[r, g, b], 0-255");

const lineWeightSchema = z.number().int().min(1).max(16).describe("Pen weight 1-16");

const patternNameSchema = z.string().min(1).max(256);

export const graphicOverridesSchema = z
  .object({
    halftone: z.boolean().optional(),
    transparency: z.number().int().min(0).max(100).optional().describe("Surface transparency 0-100"),
    projectionLineColor: rgbSchema.optional(),
    projectionLineWeight: lineWeightSchema.optional(),
    projectionLinePattern: patternNameSchema.optional().describe("Line pattern name, or 'Solid'"),
    cutLineColor: rgbSchema.optional(),
    cutLineWeight: lineWeightSchema.optional(),
    cutLinePattern: patternNameSchema.optional().describe("Line pattern name, or 'Solid'"),
    surfaceForegroundColor: rgbSchema.optional().describe("Surface fill colour (solid fill if no pattern is given)"),
    surfaceForegroundPattern: patternNameSchema.optional().describe("Fill pattern name, e.g. 'Solid fill'"),
    cutForegroundColor: rgbSchema.optional().describe("Cut fill colour (solid fill if no pattern is given)"),
    cutForegroundPattern: patternNameSchema.optional().describe("Fill pattern name, e.g. 'Solid fill'"),
    visible: z.boolean().optional().describe("Show or hide the targets in the view"),
  })
  .strict();
