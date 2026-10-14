import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { elementFilterShape, levelListSchema } from "../utils/elementFilterSchemas.js";

export const snapToGridShape = {
  categories: elementFilterShape.categories.describe(
    'Categories to snap (default ["StructuralColumns","StructuralFoundation","Walls"]); add "StructuralFraming" for beams. Point-based families (columns, footings, pile caps, piles) and straight line-based elements (walls, beams) are supported'
  ),
  elementIds: elementFilterShape.elementIds,
  levels: levelListSchema.optional(),
  typeNameContains: z
    .string()
    .min(1)
    .max(256)
    .optional()
    .describe('Keep elements whose type name contains this text (case-insensitive), e.g. "C1" or "600x600"'),
  commentsStartsWith: elementFilterShape.commentsStartsWith,
  markStartsWith: elementFilterShape.markStartsWith,
  axes: z
    .enum(["grid", "global"])
    .optional()
    .default("grid")
    .describe(
      "grid (default): round the element's offset from the nearest grid running across each local axis (rotated elements and inclined grids work); global: round plain X / Y coordinates relative to the project base point (or globalOrigin:'internal')"
    ),
  globalOrigin: z
    .enum(["projectBasePoint", "internal"])
    .optional()
    .describe("axes:'global' only: round relative to the project base point (default) or the internal origin"),
  stepMm: z
    .number()
    .positive()
    .max(1000)
    .optional()
    .default(50)
    .describe("Rounding step in mm (default 50; 5, 10 or 25 for finer setting out). A column at 545 mm from grid A becomes 550"),
  maxShiftMm: z
    .number()
    .min(0)
    .max(1000)
    .optional()
    .default(30)
    .describe("Largest move applied per axis (default 30 mm). Larger differences are deliberate offsets or real errors: they are reported (tooLargeSamples), not snapped"),
  maxGridDistanceMm: z
    .number()
    .positive()
    .max(1000000)
    .optional()
    .default(5000)
    .describe("Only grids within this distance of the element are used as reference (default 5000 mm); axes with no grid in reach are left unchanged"),
  wallMode: z
    .enum(["run", "acrossOnly", "ends"])
    .optional()
    .default("run")
    .describe(
      "Walls only. run (default): joined / touching collinear walls form a run that snaps across as one line; joined ends are then rebuilt from the snapped lines so corners and T-junctions stay closed, and free ends snap along. " +
        "acrossOnly: each straight wall shifts perpendicular to itself only (length and ends along the wall unchanged). ends: across plus each end along independently (as for beams; may open gaps at corners)"
    ),
  dryRun: z
    .boolean()
    .optional()
    .default(true)
    .describe("Default true: only report per category how many elements would move, max/avg shift and samples. Set false to move them (one undo step)"),
};

export function registerSnapToGridTool(server: McpServer) {
  server.tool(
    "snap_to_grid",
    "Clean small position errors of extracted / traced models so drawings read round numbers (550 | 550 instead of 5 | 1095): for each selected element and each of its local plan axes, the offset from the nearest grid running across that axis is rounded to stepMm (default 50) and the element is moved by the difference when it is at most maxShiftMm (default 30). " +
      "Point-based families (columns, footings, pile caps, piles) snap their insertion point along their own X/Y; straight beams snap across their location line and each end point along it. Walls follow wallMode: run (default) snaps each run of joined collinear walls across together and rebuilds joined corners from the snapped lines so they stay closed; acrossOnly only shifts walls perpendicular to themselves; ends snaps like beams. axes:'global' rounds X/Y coordinates instead. " +
      "dryRun defaults to true and reports per category the count, wouldMove, max/avg shift, elements whose offset exceeds maxShiftMm and samples (grid, offset, snapped value). Run it before dimension_elements / tag_elements. Pinned and grouped elements are skipped.",
    snapToGridShape,
    async (args) => sendDocumentationCommand("snap_to_grid", args, 600000)
  );
}
