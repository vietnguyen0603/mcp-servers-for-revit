import { z } from "zod";

/** Options shared by the manual detail-reference tools. */
export const detailReferenceOptionsShape = {
  familyNameContains: z
    .string()
    .min(1)
    .max(256)
    .optional()
    .describe("Bubble family name filter, case-insensitive (default 'Section Cut')"),
  detailNumberParam: z.string().min(1).max(256).optional().describe("Default 'Detail Number'"),
  sheetNumberParam: z.string().min(1).max(256).optional().describe("Default 'Sheet Number'"),
};

export const sheetDetailPairSchema = z
  .object({
    sheetNumber: z.string().min(1).max(64),
    detailNumber: z.string().min(1).max(64),
  })
  .strict();

/** Renders a local validation failure the same way as a Revit failure. */
export function validationError(command: string, message: string) {
  return {
    content: [{ type: "text" as const, text: `${command} failed: ${message}` }],
    isError: true,
  };
}
