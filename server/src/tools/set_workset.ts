import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { elementFilterShape, hasSelection, levelListSchema } from "../utils/elementFilterSchemas.js";

export const setWorksetShape = {
  listWorksets: z
    .boolean()
    .optional()
    .default(false)
    .describe("true: only list the user worksets (id, name, open, editable, default, element count)"),
  workset: z.string().min(1).max(256).optional().describe("Target workset name"),
  createIfMissing: z.boolean().optional().default(true).describe("Create the workset when it does not exist (default true)"),
  enableWorksharing: z
    .boolean()
    .optional()
    .default(false)
    .describe(
      "When the document is not workshared: true enables worksharing first (worksets 'Shared Levels and Grids' and 'Workset1'; cannot be undone). Default false -> error"
    ),
  ...elementFilterShape,
  levels: levelListSchema.optional(),
};

const setWorksetSchema = z.object(setWorksetShape);
export type SetWorksetArgs = z.infer<typeof setWorksetSchema>;

export function validateSetWorkset(args: SetWorksetArgs): string | undefined {
  if (args.listWorksets) return undefined;
  if (!args.workset) return "give workset (or listWorksets: true).";
  if (!hasSelection(args)) return "give categories and/or elementIds.";
  return undefined;
}

export function registerSetWorksetTool(server: McpServer) {
  server.tool(
    "set_workset",
    "Move model elements to a workset in a workshared document (sets the element Workset parameter), creating the workset if missing. " +
      "Select with categories and/or elementIds, narrowed by levels, commentsEquals/commentsStartsWith/markStartsWith/typeNameEquals. " +
      "listWorksets:true lists the user worksets instead. A non-workshared document is an error unless enableWorksharing:true. " +
      "Reports counts per category: changed, already on the workset, and failures (read-only / not editable).",
    setWorksetShape,
    async (args) => {
      const problem = validateSetWorkset(args);
      if (problem) {
        return { content: [{ type: "text" as const, text: `set_workset: ${problem}` }], isError: true };
      }
      return sendDocumentationCommand("set_workset", args, 600000);
    }
  );
}
