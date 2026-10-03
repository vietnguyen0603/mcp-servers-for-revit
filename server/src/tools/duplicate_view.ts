import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerDuplicateViewTool(server: McpServer) {
  server.tool(
    "duplicate_view",
    "Duplicate Revit views as a plain copy (Duplicate), a copy including annotation (WithDetailing) or a dependent view (AsDependent), optionally naming each copy.",
    {
      views: z
        .array(
          z.object({
            viewId: elementIdSchema.describe("View to duplicate"),
            mode: z.enum(["Duplicate", "WithDetailing", "AsDependent"]).optional().default("Duplicate"),
            name: z.string().min(1).max(256).optional().describe("Name for the copy"),
          })
        )
        .min(1)
        .max(100),
    },
    async (args) => sendDocumentationCommand("duplicate_view", args)
  );
}
