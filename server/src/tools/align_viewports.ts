import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerAlignViewportsTool(server: McpServer) {
  server.tool(
    "align_viewports",
    "Align viewports, on the same or other sheets, to a reference viewport. ViewOrigin (default) moves each viewport so the same model point sits at the same sheet position, e.g. stacking floor plans of every level; it needs the same scale and view direction and Revit 2023 or later. Center copies the reference viewport's box centre.",
    {
      referenceViewportId: elementIdSchema.describe("Viewport to align to (see list_sheets)"),
      viewportIds: z.array(elementIdSchema).min(1).max(500).describe("Viewports to move"),
      align: z.enum(["ViewOrigin", "Center"]).optional().default("ViewOrigin"),
    },
    async (args) => sendDocumentationCommand("align_viewports", args)
  );
}
