import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { cadRegionSchema, layerPatternsSchema, regionModeSchema } from "../utils/cadSchemas.js";

export const getCadGeometryShape = {
  importId: elementIdSchema
    .optional()
    .describe("ImportInstance id (from link_cad). Omit to list the CAD imports/links of the document (or of viewId)"),
  viewId: elementIdSchema
    .optional()
    .describe(
      "Read the geometry as seen in this view (default: the owner view of a view-specific import, else model geometry); when listing, only imports visible in this view"
    ),
  summary: z
    .boolean()
    .optional()
    .default(false)
    .describe("Only counts per layer and kind, extents and z range - no coordinates. Use first on large drawings"),
  layers: layerPatternsSchema.optional().describe("Only these layers (wildcards)"),
  excludeLayers: layerPatternsSchema.optional().describe('Skip these layers (wildcards), e.g. ["*TEXT*","DEFPOINTS"]'),
  region: cadRegionSchema.optional(),
  regionMode: regionModeSchema,
  kinds: z
    .array(z.enum(["lines", "arcs", "circles", "polylines", "curves", "solids"]))
    .min(1)
    .max(6)
    .optional()
    .describe("Geometry kinds (default all except solids). curves = ellipses/splines tessellated; solids = hatch/solid fills, counted only"),
  maxItems: z
    .number()
    .int()
    .min(1)
    .max(50000)
    .optional()
    .default(5000)
    .describe("Cap on returned coordinate items across all layers (default 5000); truncated:true when hit"),
};

export function registerGetCadGeometryTool(server: McpServer) {
  server.tool(
    "get_cad_geometry",
    "Read a CAD import/link's geometry in MODEL coordinates (instance and nested block transforms applied), grouped by layer, in mm rounded to 0.1. " +
      "Compact arrays: lines [x1,y1,x2,y2], arcs [x1,y1,x2,y2,xm,ym] (start, end, mid point), circles [cx,cy,r], polylines and curves (ellipse/spline, tessellated) [x,y,x,y,...]; hatches/solids are only counted. " +
      "Filter by layers/excludeLayers (wildcards), region (model mm) and kinds; summary:true returns counts per layer and extents only. Without importId it lists the imports (id, name, linked, owner view, pinned). " +
      "CAD texts (TEXT/MTEXT) are NOT exposed by the Revit API - use pdf_extract on a PDF of the drawing for labels. Read-only.",
    getCadGeometryShape,
    async (args) => sendDocumentationCommand("get_cad_geometry", args, 180000)
  );
}
