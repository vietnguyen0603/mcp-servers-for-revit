import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import {
  elementIdSchema,
  parameterValuesSchema,
  point3Schema,
  sendDocumentationCommand,
} from "../utils/documentationSchemas.js";

const componentSchema = z
  .object({
    familyTypeId: elementIdSchema.optional().describe("Detail component type id"),
    familyName: z.string().min(1).max(256).optional(),
    typeName: z.string().min(1).max(256).optional(),
    location: point3Schema.optional().describe("Insertion point for point-based detail items (mm)"),
    start: point3Schema.optional().describe("Start point for line-based detail items (mm)"),
    end: point3Schema.optional().describe("End point for line-based detail items (mm)"),
    rotationDegrees: z.number().finite().optional().describe("Rotation of point-based items about the view normal"),
    parameters: parameterValuesSchema.optional().describe("Instance parameter name → value"),
  })
  .superRefine((component, ctx) => {
    if (component.familyTypeId === undefined && !component.familyName && !component.typeName) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, message: "Provide familyTypeId, or familyName and/or typeName" });
    }
    if (!component.location && !(component.start && component.end)) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, message: "Provide location (point-based) or start and end (line-based)" });
    }
  });

export function registerPlaceDetailComponentTool(server: McpServer) {
  server.tool(
    "place_detail_component",
    "Place detail component families (Detail Items) in a view. Point-based families use location and optional rotation; line-based families use start and end. Coordinates are millimetres projected onto the view plane. Instance parameters may be set per component; numeric values use project display units.",
    {
      viewId: elementIdSchema.optional().describe("View (default active view)"),
      components: z.array(componentSchema).min(1).max(500),
    },
    async (args) => sendDocumentationCommand("place_detail_component", args)
  );
}
