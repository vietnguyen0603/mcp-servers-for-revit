import { z } from "zod";

/** Text fields shared by create_dimensions and modify_annotations(setDimensionText). */
const textFields = {
  override: z
    .string()
    .max(512)
    .optional()
    .describe("Replaces the measured value, e.g. 'EQ', 'D/3', '\"b\"', 'SEE PLAN'. Empty string clears"),
  prefix: z.string().max(256).optional().describe("Text before the value. Empty string clears"),
  suffix: z.string().max(256).optional().describe("Text after the value, e.g. 'TYP', 'MIN', 'CLR'. Empty string clears"),
  above: z.string().max(256).optional().describe("Text above the dimension line. Empty string clears"),
  below: z.string().max(256).optional().describe("Text below the dimension line, e.g. 'TYP'. Empty string clears"),
};

export const dimensionTextSchema = z
  .object(textFields)
  .strict()
  .describe(
    "Dimension text. On a multi-segment dimension it is applied to every segment; use 'segments' for individual ones"
  );

export const dimensionSegmentTextSchema = z
  .object({
    index: z.number().int().min(0).max(999).describe("0-based segment index in chain order"),
    ...textFields,
  })
  .strict();

export const dimensionSegmentsTextSchema = z
  .array(dimensionSegmentTextSchema)
  .min(1)
  .max(500)
  .describe("Per-segment text for multi-segment (chain) dimensions; index 0 on a single-segment dimension targets it");
