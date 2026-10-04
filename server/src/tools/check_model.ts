import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";
import {
  MODEL_QA_TIMEOUT_MS,
  categoryNameSchema,
  levelNamesSchema,
  outFileSchema,
} from "../utils/modelQaSchemas.js";

export const CHECK_MODEL_CHECKS = ["counts", "elevations", "overlaps", "unsupported", "levelsWithoutFloor"] as const;

export const clashPairSchema = z
  .object({
    a: categoryNameSchema.describe("First category, e.g. 'StructuralFraming'"),
    b: categoryNameSchema.describe("Second category, e.g. 'Walls'"),
  })
  .strict();

export const checkModelShape = {
  checks: z
    .array(z.enum(CHECK_MODEL_CHECKS))
    .min(1)
    .max(CHECK_MODEL_CHECKS.length)
    .optional()
    .default([...CHECK_MODEL_CHECKS])
    .describe(
      "Sections to run (default all): counts, elevations, overlaps, unsupported, levelsWithoutFloor"
    ),
  categories: z
    .array(categoryNameSchema)
    .min(1)
    .max(20)
    .optional()
    .describe(
      "Categories to check (default Walls, StructuralColumns, StructuralFraming, Floors, StructuralFoundation)"
    ),
  levels: levelNamesSchema.optional().describe("Only elements on these levels (level names)"),
  byType: z.boolean().optional().default(false).describe("counts: also break counts down per type"),
  sameCategoryOverlaps: z
    .boolean()
    .optional()
    .default(true)
    .describe("overlaps: report overlapping elements of the same category (duplicates, wall corners, stacked beams)"),
  clashPairs: z
    .array(clashPairSchema)
    .max(20)
    .optional()
    .describe("overlaps: cross-category clashes to report, e.g. [{a:'StructuralFraming', b:'Walls'}]"),
  minOverlapVolumeM3: z
    .number()
    .min(0)
    .max(1000)
    .optional()
    .default(0.01)
    .describe("overlaps: ignore intersections smaller than this volume (m3)"),
  skipJoined: z
    .boolean()
    .optional()
    .default(true)
    .describe("overlaps: skip pairs joined with Join Geometry (their solids are already cut)"),
  supportToleranceMm: z
    .number()
    .min(0)
    .max(5000)
    .optional()
    .default(300)
    .describe("unsupported: max gap (mm) between a beam end / column base and its support"),
  maxItems: z
    .number()
    .int()
    .min(1)
    .max(10000)
    .optional()
    .default(200)
    .describe("Max listed items per list section (overlaps, unsupported); totals are always complete"),
  outFile: outFileSchema
    .optional()
    .describe(
      "Absolute .json path on the Revit machine: write the full uncapped report there and return only totals"
    ),
};

export function registerCheckModelTool(server: McpServer) {
  server.tool(
    "check_model",
    "Read-only model QA in one call for structural models. Sections (checks): " +
      "counts - element counts per category x level (and per type with byType); " +
      "elevations - per category + type + level: count and min/max of bounding-box bottom and top elevations in mm, absolute and relative to the level (verify 'every cap top at level', 'pile tops 3000 under cap top'); " +
      "overlaps - same-category overlaps (duplicated or stacked elements, wall corners overlapping, beam inside beam) and cross-category clashes for clashPairs (e.g. StructuralFraming vs Walls), measured as solid intersection volume >= minOverlapVolumeM3 (default 0.01 m3) after a bounding-box sweep, joined pairs skipped by default; each item gives both ids, the volume and the share of the smaller element; " +
      "unsupported - beams whose ends are not within supportToleranceMm (default 300) of a column, wall, foundation or another beam, and columns with no column, wall, beam, foundation or floor under their base; " +
      "levelsWithoutFloor - levels with no floor/slab. " +
      "Filter with categories and levels. Lists are capped at maxItems (totals are always complete); give outFile to write the full JSON on the Revit machine and get only totals back. Can take minutes on large models.",
    checkModelShape,
    async (args) => sendDocumentationCommand("check_model", args, MODEL_QA_TIMEOUT_MS)
  );
}
