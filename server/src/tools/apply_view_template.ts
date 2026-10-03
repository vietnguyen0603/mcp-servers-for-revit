import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerApplyViewTemplateTool(server: McpServer) {
  server.tool(
    "apply_view_template",
    "Assign a view template to views (the template then controls them), apply the template's properties once without keeping it assigned (applyPropertiesOnly), or remove the assigned template (remove=true). Find template ids with list_views includeTemplates=true. Per-view errors are reported without discarding the others.",
    {
      viewIds: z.array(elementIdSchema).min(1).max(1000).describe("Views to update"),
      templateId: elementIdSchema.optional().describe("View template to apply"),
      remove: z.boolean().optional().default(false).describe("Remove the assigned template instead"),
      applyPropertiesOnly: z
        .boolean()
        .optional()
        .default(false)
        .describe("Copy the template's properties once without assigning it"),
    },
    async (args) => {
      if (!args.remove && args.templateId === undefined) {
        return {
          content: [{ type: "text" as const, text: "apply_view_template requires templateId unless remove is true." }],
          isError: true,
        };
      }
      return sendDocumentationCommand("apply_view_template", args);
    }
  );
}
