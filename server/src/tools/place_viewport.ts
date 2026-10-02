import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point2Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerPlaceViewportTool(server: McpServer) {
  server.tool(
    "place_viewport",
    "Place views on sheets. Ordinary views become viewports centred at `center`; schedules become schedule instances with their top-left corner at `center`. Sheet coordinates are millimetres and default to the centre of the sheet. A non-legend view can only be placed on one sheet.",
    {
      viewports: z
        .array(
          z.object({
            sheetId: elementIdSchema.describe("Target sheet"),
            viewId: elementIdSchema.describe("View or schedule to place"),
            center: point2Schema.optional().describe("Position on the sheet in mm"),
            viewportTypeId: elementIdSchema.optional().describe("Viewport type to apply"),
            rotation: z.enum(["None", "Clockwise", "Counterclockwise"]).optional(),
          })
        )
        .min(1)
        .max(200),
    },
    async (args) => sendDocumentationCommand("place_viewport", args)
  );
}
