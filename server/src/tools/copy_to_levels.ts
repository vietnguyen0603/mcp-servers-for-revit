import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { levelRefSchema } from "../utils/foundationSchemas.js";
import { elementFilterShape, hasSelection } from "../utils/elementFilterSchemas.js";

export const levelRangeSchema = z
  .object({
    from: levelRefSchema.describe("First level of the range (name, id or elevation mm)"),
    to: levelRefSchema.describe("Last level of the range (inclusive)"),
  })
  .strict()
  .describe("All levels between from and to (inclusive, by elevation order); the source level is excluded");

export const targetLevelsSchema = z.union([z.array(levelRefSchema).min(1).max(300), levelRangeSchema]);

export const copyToLevelsShape = {
  sourceLevel: levelRefSchema.describe("Level whose elements are copied (name, id or elevation mm)"),
  targetLevels: targetLevelsSchema.describe(
    'Target levels: an array of names/ids/elevations, or {from, to} e.g. {"from":"L5","to":"L18"}'
  ),
  ...elementFilterShape,
  skipExisting: z
    .boolean()
    .optional()
    .default(true)
    .describe("Skip an element when the target level already has one of the same category + type at the same plan location (idempotent re-runs). Default true"),
  returnIds: z.boolean().optional().default(false).describe("Include the new element ids per target level (default false)"),
};

const copyToLevelsSchema = z.object(copyToLevelsShape);
export type CopyToLevelsArgs = z.infer<typeof copyToLevelsSchema>;

export function registerCopyToLevelsTool(server: McpServer) {
  server.tool(
    "copy_to_levels",
    "Copy model elements of a source level onto other levels (typical floors), like Paste Aligned > Selected Levels: each copy is re-hosted on its target level with the same offsets. " +
      "Beams keep reference level + start/end offsets, floors level + height offset, columns and walls base/top level shifted by the same number of levels (walls whose top level would fall outside the project become unconnected with the same height), level-based families keep level + offset. " +
      "Select by categories and/or elementIds, narrowed by commentsEquals/commentsStartsWith/markStartsWith/typeNameEquals; only elements associated with the source level are copied (elements in groups are skipped). " +
      "skipExisting (default true) makes re-runs idempotent. One undo step per target level; reports per-level counts per category, skipped, elevation corrections and warnings.",
    copyToLevelsShape,
    async (args) => {
      if (!hasSelection(args)) {
        return {
          content: [{ type: "text" as const, text: "copy_to_levels: give categories and/or elementIds." }],
          isError: true,
        };
      }
      return sendDocumentationCommand("copy_to_levels", args, 1800000);
    }
  );
}
