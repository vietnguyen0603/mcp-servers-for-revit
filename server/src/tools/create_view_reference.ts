import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point3Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerCreateViewReferenceTool(server: McpServer) {
  server.tool(
    "create_view_reference",
    "Place live references to existing views so the marker always shows the target's current detail and sheet number. kind 'callout' draws a reference callout rectangle between `start` and `end` (opposite corners); kind 'section' draws a reference section line from head `start` to tail `end`. Coordinates are model millimetres in the parent view's plane (drafting views: XY). Parent views may be plans, sections, elevations, detail or drafting views; drafting views can always be targeted, other targets must be compatible (cropped detail/section/elevation/plan views). The 'View Reference' annotation family cannot be created through the Revit API, so this tool does not create OST_ReferenceViewer elements. Batch is one undo step with per-item results.",
    {
      references: z
        .array(
          z
            .object({
              parentViewId: elementIdSchema.describe("View the marker is drawn in"),
              targetViewId: elementIdSchema.describe("Existing view to reference"),
              kind: z.enum(["callout", "section"]).optional().default("callout"),
              start: point3Schema.describe("Callout corner or section head (mm)"),
              end: point3Schema.describe("Opposite callout corner or section tail (mm)"),
            })
            .strict()
        )
        .min(1)
        .max(100),
    },
    async (args) => sendDocumentationCommand("create_view_reference", args)
  );
}
