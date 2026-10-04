import { z } from "zod";

/**
 * Shared inputs for join_elements and check_model: model categories by a
 * friendly name or a BuiltInCategory name, normalised to "OST_..." on the wire.
 */

const FRIENDLY_CATEGORIES: Record<string, string> = {
  walls: "OST_Walls",
  wall: "OST_Walls",
  structuralcolumns: "OST_StructuralColumns",
  structuralcolumn: "OST_StructuralColumns",
  columns: "OST_StructuralColumns",
  column: "OST_StructuralColumns",
  structuralframing: "OST_StructuralFraming",
  framing: "OST_StructuralFraming",
  beams: "OST_StructuralFraming",
  beam: "OST_StructuralFraming",
  floors: "OST_Floors",
  floor: "OST_Floors",
  slabs: "OST_Floors",
  slab: "OST_Floors",
  structuralfoundation: "OST_StructuralFoundation",
  structuralfoundations: "OST_StructuralFoundation",
  foundations: "OST_StructuralFoundation",
  foundation: "OST_StructuralFoundation",
};

/** Normalises a category name; returns undefined when it is not recognised. */
export function normalizeCategory(name: string): string | undefined {
  const trimmed = name.trim();
  if (/^OST_[A-Za-z0-9_]+$/i.test(trimmed)) return "OST_" + trimmed.slice(4);
  return FRIENDLY_CATEGORIES[trimmed.replace(/[\s_-]+/g, "").toLowerCase()];
}

export const categoryNameSchema = z
  .string()
  .min(1)
  .max(128)
  .transform((value, ctx) => {
    const normalized = normalizeCategory(value);
    if (!normalized) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        message: `Unknown category '${value}'. Use Walls, StructuralColumns, StructuralFraming, Floors, StructuralFoundation or an OST_ name`,
      });
      return z.NEVER;
    }
    return normalized;
  });

export const levelNamesSchema = z
  .array(z.string().min(1).max(256))
  .min(1)
  .max(500)
  .describe("Level names to scope to (exact, case-insensitive)");

export const modelElementIdsSchema = z.array(z.number().int().positive()).min(1).max(100000);

/** Absolute Windows path of a .json file on the Revit machine. */
export const outFileSchema = z
  .string()
  .min(5)
  .max(260)
  .regex(/^([A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+)/, "outFile must be an absolute Windows path, e.g. C:\\Temp\\check.json")
  .refine((file) => !/[<>"|?*\u0000-\u001f]/.test(file.slice(2)), { message: "outFile contains invalid path characters" })
  .refine((file) => /\.json$/i.test(file), { message: "outFile must end with .json" });

/** Long-running model-wide commands: generous socket timeout (the command set times out at 10 minutes). */
export const MODEL_QA_TIMEOUT_MS = 660000;
