import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point2Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

const cropSchema = z
  .object({
    viewId: elementIdSchema,
    rectangle: z
      .object({ min: point2Schema, max: point2Schema })
      .optional()
      .describe(
        "Crop rectangle in mm. Plans: model XY. Sections, elevations, callouts and 3D: crop-box local coordinates (x along the view's right direction, y up; for sections made by create_view, y is the elevation)."
      ),
    fitToElementIds: z
      .array(elementIdSchema)
      .min(1)
      .max(5000)
      .optional()
      .describe("Fit the crop to the union of these elements' bounding boxes"),
    marginMm: z.number().min(0).max(100000).optional().describe("Margin around fitted elements (default 500)"),
    cropActive: z.boolean().optional().describe("Crop the view"),
    cropVisible: z.boolean().optional().describe("Show the crop region boundary"),
    annotationCrop: z.boolean().optional().describe("Enable the annotation crop"),
    annotationOffsetsMm: z
      .object({
        left: z.number().min(0).optional(),
        right: z.number().min(0).optional(),
        top: z.number().min(0).optional(),
        bottom: z.number().min(0).optional(),
      })
      .optional()
      .describe("Annotation crop offsets from the model crop, as Revit stores them (mm)"),
  })
  .refine((view) => !(view.rectangle && view.fitToElementIds), {
    message: "Give either rectangle or fitToElementIds, not both",
  });

export function registerSetCropRegionTool(server: McpServer) {
  server.tool(
    "set_crop_region",
    "Set the rectangular crop region of views from an explicit rectangle or by fitting it to elements, and toggle crop, crop visibility and annotation crop. Any non-rectangular crop shape is reset when a new rectangle is applied; the crop depth (near/far clip) is preserved. All lengths in millimetres.",
    {
      views: z.array(cropSchema).min(1).max(200),
    },
    async (args) => sendDocumentationCommand("set_crop_region", args)
  );
}
