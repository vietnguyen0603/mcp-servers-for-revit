import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerDeleteOrphanedTagsTool(server: McpServer) {
  server.tool(
    "delete_orphaned_tags",
    "Find tags and room tags that no longer reference an element (orphaned) in the given views or the whole project. dryRun defaults to true and only reports them; with dryRun false they are deleted in one undoable transaction. Tags of linked elements are only included when Revit itself reports them as orphaned.",
    {
      viewIds: z
        .array(elementIdSchema)
        .max(1000)
        .optional()
        .describe("Views to clean (default: every view in the project)"),
      dryRun: z.boolean().optional().default(true).describe("Report only; set false to delete"),
    },
    async (args) => sendDocumentationCommand("delete_orphaned_tags", args)
  );
}
