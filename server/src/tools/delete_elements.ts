import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { elementFilterShape, hasSelection, levelListSchema } from "../utils/elementFilterSchemas.js";

export const deleteElementsShape = {
  ...elementFilterShape,
  levels: levelListSchema.optional(),
  dryRun: z
    .boolean()
    .optional()
    .default(true)
    .describe("Default true: only report what would be deleted (counts per category and level). Set false to delete"),
};

const deleteElementsSchema = z.object(deleteElementsShape);
export type DeleteElementsArgs = z.infer<typeof deleteElementsSchema>;

export function registerDeleteElementsTool(server: McpServer) {
  server.tool(
    "delete_elements",
    "Bulk-delete model elements by filter, for clean idempotent re-runs: categories and/or elementIds (one is required), narrowed by levels, commentsEquals/commentsStartsWith/markStartsWith/typeNameEquals (all filters are ANDed). " +
      "dryRun defaults to true and only reports counts per category and per level plus sample ids; pass dryRun:false to delete in one undo step. " +
      "The result reports matched and deleted counts (deleted includes dependent elements Revit removes with them). For a few known ids, delete_element also works.",
    deleteElementsShape,
    async (args) => {
      if (!hasSelection(args)) {
        return {
          content: [{ type: "text" as const, text: "delete_elements: give categories and/or elementIds." }],
          isError: true,
        };
      }
      return sendDocumentationCommand("delete_elements", args, 600000);
    }
  );
}
