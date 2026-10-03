import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point2Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { detailNumberSchema, viewportAnchorSchema } from "../utils/viewportSchemas.js";

export function registerPlaceViewportTool(server: McpServer) {
  server.tool(
    "place_viewport",
    "Place views on sheets. Ordinary views become viewports centred at `center`; schedules become schedule instances with their top-left corner at `center`. Sheet coordinates are millimetres and default to the centre of the sheet. A non-legend view can only be placed on one sheet. " +
      "Optionally, in the same step, anchor a view point (view model mm) to an exact sheet point and set the detail number; use update_viewports to change viewports that are already placed.",
    {
      viewports: z
        .array(
          z.object({
            sheetId: elementIdSchema.describe("Target sheet"),
            viewId: elementIdSchema.describe("View or schedule to place"),
            center: point2Schema.optional().describe("Position on the sheet in mm"),
            viewportTypeId: elementIdSchema.optional().describe("Viewport type to apply"),
            rotation: z.enum(["None", "Clockwise", "Counterclockwise"]).optional(),
            anchor: viewportAnchorSchema
              .optional()
              .describe(
                "After placing, move the viewport so viewPoint (view model mm) lands on sheetPoint (sheet mm); overrides center. Not for schedules"
              ),
            detailNumber: detailNumberSchema
              .optional()
              .describe("Detail number for the new viewport; the item fails if another viewport on the sheet uses it. Not for schedules"),
          })
        )
        .min(1)
        .max(200),
    },
    async (args) => sendDocumentationCommand("place_viewport", args)
  );
}
