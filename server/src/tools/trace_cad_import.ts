import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { cadRegionSchema, layerPatternsSchema, regionModeSchema } from "../utils/cadSchemas.js";

export const traceCadImportShape = {
  importId: elementIdSchema.describe("ImportInstance to trace (see get_cad_geometry without importId)"),
  viewId: elementIdSchema
    .optional()
    .describe("View to draw in (default: the import's owner view if view-specific, else the active view)"),
  lineKind: z
    .enum(["detail", "model"])
    .optional()
    .default("detail")
    .describe(
      "detail: detail lines in the view plane (drafting/plan/section views, default); model: model lines on horizontal sketch planes at the CAD elevation"
    ),
  lineStyleMap: z
    .record(z.string().min(1).max(256), z.string().min(1).max(256))
    .refine((m) => Object.keys(m).length <= 500, { message: "At most 500 map entries" })
    .optional()
    .describe('Layer -> line style, wildcards allowed, exact names win, e.g. {"S-CONC":"PEN4","*HIDDEN*":"Hidden"}'),
  defaultLineStyle: z
    .string()
    .min(1)
    .max(256)
    .optional()
    .describe("Style for layers without a map entry and without a line style of the same name (default: Revit's default line style)"),
  layers: layerPatternsSchema.optional().describe("Only these layers (wildcards)"),
  excludeLayers: layerPatternsSchema.optional().describe("Skip these layers (wildcards)"),
  visibleLayersOnly: z.boolean().optional().default(true).describe("Skip layers hidden in the view's V/G (default true)"),
  region: cadRegionSchema.optional(),
  regionMode: regionModeSchema,
  minLength: z.number().nonnegative().finite().optional().default(0).describe("Skip segments shorter than this (mm)"),
  maxCurves: z.number().int().min(1).max(100000).optional().default(20000).describe("Cap on curves drawn (default 20000)"),
  deleteImport: z
    .boolean()
    .optional()
    .default(false)
    .describe("Delete the import instance after tracing (only when at least one line was created)"),
  dryRun: z
    .boolean()
    .optional()
    .default(true)
    .describe("Default true: report curves per layer and the chosen line styles without drawing. Set false to draw"),
};

export function registerTraceCadImportTool(server: McpServer) {
  server.tool(
    "trace_cad_import",
    "Convert a CAD import/link into native Revit lines (the API cannot explode imports): every line, arc, circle, polyline segment and spline of the import is redrawn as a detail line in the view (drafting/plan/section) or a model line. " +
      "Line style per layer: lineStyleMap entry (wildcards), else a line style with the same name as the layer, else defaultLineStyle, else Revit's default. Hatches and texts are not traced. " +
      "dryRun defaults to true and reports curves per layer and the chosen styles; with dryRun:false the lines are drawn in one undo step and deleteImport:true removes the import (use purge_cad_leftovers afterwards for its type).",
    traceCadImportShape,
    async (args) => sendDocumentationCommand("trace_cad_import", args, 600000)
  );
}
