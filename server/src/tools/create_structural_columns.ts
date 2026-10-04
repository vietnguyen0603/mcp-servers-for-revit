import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { bulkInputShape } from "../utils/bulkInput.js";
import {
  MAX_INLINE_ITEMS,
  hasTypeRef,
  levelRefSchema,
  markSchema,
  runFramingBulk,
  typeRefMessage,
  typeRefShape,
} from "../utils/framingSchemas.js";

export const columnItemSchema = z
  .object({
    ...typeRefShape,
    x: z.number().finite().describe("Column location X in mm"),
    y: z.number().finite().describe("Column location Y in mm"),
    baseLevel: levelRefSchema,
    topLevel: levelRefSchema.optional(),
    baseOffset: z.number().finite().optional().describe("Base offset from baseLevel in mm (default 0)"),
    topOffset: z.number().finite().optional().describe("Top offset from the top level in mm (default 0)"),
    height: z
      .number()
      .positive()
      .optional()
      .describe(
        "Used when topLevel is omitted: the top attaches to the level exactly at base+height (within 1 mm), else the column gets that height from its base level"
      ),
    rotationDeg: z.number().finite().optional().describe("Rotation about the column axis in degrees"),
    mark: markSchema.optional(),
    comments: z.string().max(1024).optional(),
  })
  .strict()
  .refine(hasTypeRef, typeRefMessage);

export function registerCreateStructuralColumnsTool(server: McpServer) {
  server.tool(
    "create_structural_columns",
    "Place structural columns by plan point (mm) between levels - built for whole buildings (thousands of columns in one call; pass big sets as a CSV/JSON dataFile). " +
      "Type by typeId or familyName + typeName (create sizes first with create_family_type). Levels by name, id or elevation (mm). Without topLevel: height attaches the top to the level at base+height, else an unconnected height; with neither, the top goes to the next level up. " +
      "Each item reports id, baseLevel, topLevel and warnings; failed items do not stop the others. Sent in chunks of 300 (one undo step per chunk).",
    {
      columns: z.array(columnItemSchema).max(MAX_INLINE_ITEMS).optional().describe("Columns to place"),
      ...bulkInputShape,
    },
    async (args) =>
      runFramingBulk("create_structural_columns", "columns", columnItemSchema, {
        items: args.columns,
        dataFile: args.dataFile,
        dataFormat: args.dataFormat,
        summary: args.summary,
      })
  );
}
