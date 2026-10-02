import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerListViewsTool(server: McpServer) {
  server.tool(
    "list_views",
    "List views in the current Revit project with id, name, view type, scale, level, applied view template and the sheet numbers each view is placed on. Optionally include view templates and view family types (needed for viewTemplateId / viewFamilyTypeId in create_view).",
    {
      viewTypes: z
        .array(z.string().min(1).max(64))
        .max(32)
        .optional()
        .describe(
          "Filter by Revit ViewType names, e.g. FloorPlan, CeilingPlan, EngineeringPlan (structural plans), Section, Elevation, ThreeD, DraftingView, Legend, Schedule, DrawingSheet."
        ),
      nameContains: z.string().max(256).optional().describe("Case-insensitive substring filter on the view name"),
      includeTemplates: z.boolean().optional().default(false).describe("Include view templates in the list"),
      includeViewFamilyTypes: z
        .boolean()
        .optional()
        .default(false)
        .describe("Also return view family types (id, name, viewFamily)"),
      limit: z.number().int().min(1).max(5000).optional().default(500).describe("Maximum views to return"),
    },
    async (args) => sendDocumentationCommand("list_views", args)
  );
}
