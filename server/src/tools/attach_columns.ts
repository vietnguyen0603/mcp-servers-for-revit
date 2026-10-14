import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { structuralMemberFilterSchema } from "./summarize_structural_members.js";

export const attachEndSchema = z
  .object({
    targets: z
      .union([z.literal("auto"), z.array(elementIdSchema).min(1).max(1000)])
      .default("auto")
      .describe(
        "'auto' (default): the nearest beam/floor/roof (see categories) whose geometry is above the column top (below the base for attachBase) at the column's location. " +
          "Or element ids: one id is used for every column (any target Revit accepts: beam, floor, roof, ceiling, level, ...); with several ids each column takes the nearest one it hits"
      ),
    categories: z
      .array(z.enum(["framing", "floors", "roofs", "ceilings"]))
      .min(1)
      .optional()
      .describe("targets 'auto' only: categories searched (default framing, floors, roofs)"),
    justification: z
      .enum(["minimum", "midpoint", "maximum", "tangent"])
      .default("minimum")
      .describe("Where the column ends against a non-uniform target: minimum intersection (default), midpoint, maximum, tangent (slanted columns)"),
    cutStyle: z
      .enum(["cutColumn", "cutTarget", "none"])
      .default("cutColumn")
      .describe("Which element is cut where they overlap (default cutColumn, as in Revit's Attach Top/Base)"),
    offsetMm: z.number().finite().min(-100000).max(100000).default(0).describe("Attachment offset in mm (positive = higher)"),
    searchRangeMm: z
      .number()
      .positive()
      .max(100000)
      .default(5000)
      .describe("How far beyond the column end auto search looks (default 5000 mm)"),
  })
  .strict();

export const attachColumnsShape = {
  filter: structuralMemberFilterSchema.describe(
    "Columns to process (elementIds, comments, marks, familyName/typeName, base level). Framing in the selection is ignored"
  ),
  attachTop: attachEndSchema.optional().describe("Attach the column tops"),
  attachBase: attachEndSchema.optional().describe("Attach the column bases"),
  detachTop: z.boolean().default(false).describe("Remove the top attachment"),
  detachBase: z.boolean().default(false).describe("Remove the base attachment"),
  dryRun: z.boolean().default(false).describe("Only report which target each column would attach to"),
  maxElements: z.number().int().min(1).max(20000).default(2000).describe("Refuse to run when more columns match"),
};

export function registerAttachColumnsTool(server: McpServer) {
  server.tool(
    "attach_columns",
    "Attach the top and/or base of placed structural columns to beams, floors, roofs (or any target Revit accepts: ceilings, levels, ...) like Revit's Attach Top/Base, or detach them. " +
      "targets 'auto' finds, per column, the nearest framing/floor/roof whose geometry is directly above the column top (below the base) within searchRangeMm; or pass element ids. " +
      "justification (minimum/midpoint/maximum/tangent), cutStyle and offsetMm match the Revit options. An existing attachment at that end is replaced. " +
      "Each column reports the target used (or why none) and the action; dryRun: true lists the targets without changing anything. One undo step; failed columns do not stop the others.",
    attachColumnsShape,
    async (args) => {
      if (!args.attachTop && !args.attachBase && !args.detachTop && !args.detachBase) {
        return { content: [{ type: "text" as const, text: "Give attachTop, attachBase, detachTop or detachBase." }], isError: true };
      }
      if ((args.attachTop && args.detachTop) || (args.attachBase && args.detachBase)) {
        return {
          content: [{ type: "text" as const, text: "Do not attach and detach the same end (attach already replaces an existing attachment)." }],
          isError: true,
        };
      }
      if (!Object.values(args.filter).some((v) => v !== undefined)) {
        return { content: [{ type: "text" as const, text: "Give at least one filter criterion (use category: 'columns' to process every column)." }], isError: true };
      }
      return sendDocumentationCommand("attach_columns", args, 300000);
    }
  );
}
