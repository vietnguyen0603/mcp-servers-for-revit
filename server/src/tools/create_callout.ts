import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point3Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerCreateCalloutTool(server: McpServer) {
  server.tool(
    "create_callout",
    "Create callout views inside plan, section, elevation or detail views. The callout rectangle is given by two opposite corners in model millimetres lying in the parent view's plane (for plans: model XY). Plans default to the parent's own view type; sections and elevations default to a Detail view type. Per-callout errors are reported without discarding the others, and the batch is one undo step.",
    {
      callouts: z
        .array(
          z.object({
            parentViewId: elementIdSchema.describe("View to place the callout in"),
            min: point3Schema.describe("First corner of the callout rectangle (model mm)"),
            max: point3Schema.describe("Opposite corner of the callout rectangle (model mm)"),
            viewFamilyTypeId: elementIdSchema
              .optional()
              .describe("Callout view type (see list_views includeViewFamilyTypes)"),
            name: z.string().min(1).max(256).optional(),
            scale: z.number().int().min(1).max(100000).optional().describe("Scale denominator, e.g. 20 for 1:20"),
            viewTemplateId: elementIdSchema.optional(),
          })
        )
        .min(1)
        .max(100),
    },
    async (args) => sendDocumentationCommand("create_callout", args)
  );
}
