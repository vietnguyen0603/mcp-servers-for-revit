import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point3Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

const loopSchema = z.array(point3Schema).min(3).max(1000);

export function registerCreateFilledRegionTool(server: McpServer) {
  server.tool(
    "create_filled_region",
    "Create filled regions (or masking regions, Revit 2024+) in a view or on a sheet from an outer boundary polygon and optional hole polygons, in millimetres projected onto the view plane. Choose the type by filledRegionTypeId or filledRegionTypeName (default: project default filled region type). Call with listTypes=true and no regions to list filled region types and line styles.",
    {
      viewId: elementIdSchema.optional().describe("View or sheet (default active view)"),
      listTypes: z.boolean().optional().describe("With no regions: return filled region types and line styles"),
      regions: z
        .array(
          z.object({
            boundary: loopSchema.describe("Outer loop points (mm); closing point optional"),
            holes: z.array(loopSchema).max(100).optional().describe("Inner loops (mm)"),
            filledRegionTypeId: elementIdSchema.optional(),
            filledRegionTypeName: z.string().min(1).max(256).optional(),
            masking: z.boolean().optional().describe("Create a masking region instead (Revit 2024+)"),
            lineStyle: z.string().min(1).max(256).optional().describe("Boundary line style name"),
          })
        )
        .max(500)
        .optional(),
    },
    async (args) => {
      if (!args.regions?.length && !args.listTypes) {
        return {
          content: [{ type: "text" as const, text: "create_filled_region requires regions, or listTypes=true." }],
          isError: true,
        };
      }
      return sendDocumentationCommand("create_filled_region", args);
    }
  );
}
