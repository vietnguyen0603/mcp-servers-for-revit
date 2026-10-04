import { z } from "zod";
import { elementIdSchema } from "./documentationSchemas.js";
import { levelRefSchema } from "./foundationSchemas.js";

/**
 * Shared element-selection fields for the bulk model tools (copy_to_levels,
 * delete_elements, set_workset). The Revit side ANDs every given filter.
 */

export const categoryNameSchema = z
  .string()
  .min(1)
  .max(128)
  .describe('Category: BuiltInCategory name with or without "OST_" (e.g. "StructuralFraming", "OST_Walls") or a display name ("Structural Framing")');

export const elementFilterShape = {
  categories: z
    .array(categoryNameSchema)
    .min(1)
    .max(50)
    .optional()
    .describe('Categories to select, e.g. ["StructuralFraming","Floors","StructuralColumns","Walls","StructuralFoundation","GenericModel"]'),
  elementIds: z.array(elementIdSchema).min(1).max(100000).optional().describe("Restrict to these element ids"),
  commentsEquals: z.string().max(1024).optional().describe("Keep elements whose Comments equal this text (case-sensitive)"),
  commentsStartsWith: z.string().min(1).max(1024).optional().describe('Keep elements whose Comments start with this text, e.g. "sup"'),
  markStartsWith: z.string().min(1).max(256).optional().describe("Keep elements whose Mark starts with this text"),
  typeNameEquals: z.string().min(1).max(256).optional().describe("Keep elements whose type name equals this text (case-insensitive)"),
};

export const levelListSchema = z
  .array(levelRefSchema)
  .min(1)
  .max(500)
  .describe("Levels (name, id or elevation mm); an element matches when its base/reference level is one of them");

/** True when at least one filter that narrows the selection is given. */
export function hasSelection(args: {
  categories?: unknown[];
  elementIds?: unknown[];
}): boolean {
  return (args.categories?.length ?? 0) > 0 || (args.elementIds?.length ?? 0) > 0;
}
