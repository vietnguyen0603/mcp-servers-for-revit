import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import {
  elementIdSchema,
  parameterValuesSchema,
  point3Schema,
  sendDocumentationCommand,
} from "../utils/documentationSchemas.js";

const leaderSchema = z.object({
  end: point3Schema.describe("Leader arrow end point (mm). Required: Revit's default end lands far from the symbol"),
  elbow: point3Schema.optional().describe("Leader elbow point (mm)"),
});

const symbolSchema = z
  .object({
    familyTypeId: elementIdSchema.optional().describe("Annotation symbol type id (from listTypes)"),
    familyName: z
      .string()
      .min(1)
      .max(256)
      .optional()
      .describe("Family name, or 'Family : Type'. Exact case-insensitive match first, then unique partial match"),
    typeName: z.string().min(1).max(256).optional(),
    location: point3Schema.describe("Insertion point (mm), projected onto the view plane"),
    rotationDegrees: z.number().finite().optional().describe("Rotation about the view normal, counter-clockwise"),
    parameters: parameterValuesSchema
      .optional()
      .describe('Instance parameter name → value, e.g. {"Detail": "A", "Text Above": "BASE PLATE"}. Yes/No accepts true/false or "Yes"/"No"; numbers use project display units'),
    leaders: z.array(leaderSchema).max(20).optional().describe("Leaders to add (generic annotation families that allow leaders)"),
  })
  .superRefine((symbol, ctx) => {
    if (symbol.familyTypeId === undefined && !symbol.familyName && !symbol.typeName) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, message: "Provide familyTypeId, or familyName and/or typeName" });
    }
  });

export function registerPlaceAnnotationSymbolTool(server: McpServer) {
  server.tool(
    "place_annotation_symbol",
    "Place annotation symbol families (Generic Annotations, and other view-based annotation symbols that are not tags) in a drafting/detail view or on a sheet, set their instance parameters and add leaders, in one undo step. Typical uses: sub-detail titles (e.g. '_TT-SY-Subtitle : Subtitle' with Detail='A', Text Above='BASE PLATE CONN.', Text Below, Length) and weld symbols (e.g. 'TT SF_Weld_Symbol : Both' with Top/Bottom Weld Size/Length, Field Weld, Weld All Around as Yes/No, Symbol Left, Tail Note, plus a leader to the joint); also elevation markers and grid heads drawn as symbols. Coordinates are millimetres projected onto the view plane. Call with listTypes=true and no symbols to list available family:type names, ids and their editable instance parameter names. Returns per-item results with succeeded/failed counts.",
    {
      viewId: elementIdSchema.optional().describe("View or sheet (default active view)"),
      listTypes: z.boolean().optional().describe("With no symbols: list annotation symbol types and their instance parameters"),
      allCategories: z
        .boolean()
        .optional()
        .describe("listTypes: include view-based annotation symbols outside Generic Annotations (default false)"),
      symbols: z.array(symbolSchema).max(500).optional(),
    },
    async (args) => {
      if (!args.symbols?.length && !args.listTypes) {
        return {
          content: [{ type: "text" as const, text: "place_annotation_symbol requires symbols, or listTypes=true." }],
          isError: true,
        };
      }
      return sendDocumentationCommand("place_annotation_symbol", args);
    }
  );
}
