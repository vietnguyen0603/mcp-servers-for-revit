import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import {
  elementIdSchema,
  parameterValuesSchema,
  sendDocumentationCommand,
} from "../utils/documentationSchemas.js";
import { point3LengthSchema } from "../utils/lengthInput.js";

const componentSchema = z
  .object({
    copyFromElementId: elementIdSchema
      .optional()
      .describe(
        "Copy this existing detail item (any view) instead of creating a new instance, then set start/end (line-based) or location (point-based). Use for line-based families that cannot be created directly. A family/type given as well changes the copy's type"
      ),
    familyTypeId: elementIdSchema.optional().describe("Detail component type id"),
    familyName: z.string().min(1).max(256).optional(),
    typeName: z.string().min(1).max(256).optional(),
    location: point3LengthSchema.optional().describe("Insertion point for point-based detail items (mm)"),
    start: point3LengthSchema.optional().describe("Start point for line-based detail items (mm)"),
    end: point3LengthSchema.optional().describe("End point for line-based detail items (mm)"),
    rotationDegrees: z.number().finite().optional().describe("Rotation of point-based items about the view normal (degrees; with copyFromElementId it is added to the source rotation)"),
    parameters: parameterValuesSchema.optional().describe("Instance parameter name → value"),
  })
  .superRefine((component, ctx) => {
    if (
      component.copyFromElementId === undefined &&
      component.familyTypeId === undefined &&
      !component.familyName &&
      !component.typeName
    ) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, message: "Provide familyTypeId, or familyName and/or typeName" });
    }
    if (!component.location && !(component.start && component.end)) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, message: "Provide location (point-based) or start and end (line-based)" });
    }
  });

export function registerPlaceDetailComponentTool(server: McpServer) {
  server.tool(
    "place_detail_component",
    "Place detail component families (Detail Items) in a view. Point-based families use location and optional rotation; line-based families use start and end. Coordinates are millimetres (numbers) or strings with units such as 2'-9\" or 3/4\", projected onto the view plane. copyFromElementId copies an existing detail item (e.g. a hand-placed line-based arrow or break line that Revit will not create via the API) into the view and moves it to start/end or location. Instance parameters may be set per component; numeric values use project display units.",
    {
      viewId: elementIdSchema.optional().describe("View (default active view)"),
      components: z.array(componentSchema).min(1).max(500),
    },
    async (args) => sendDocumentationCommand("place_detail_component", args)
  );
}
