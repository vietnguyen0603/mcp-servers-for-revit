import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point3Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { cadError, layerPatternsSchema } from "../utils/cadSchemas.js";

export const linkCadShape = {
  path: z
    .string()
    .min(1)
    .max(400)
    .regex(/^([A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+|\/)/, "must be an absolute path")
    .regex(/\.(dwg|dxf)$/i, "must be a .dwg or .dxf file")
    .describe("Absolute path of the DWG/DXF file (as seen by the Revit machine)"),
  mode: z
    .enum(["link", "import"])
    .optional()
    .default("link")
    .describe("link (Link CAD, reloadable; default) or import (Import CAD, embedded; required in families)"),
  viewId: elementIdSchema
    .optional()
    .describe(
      "View that receives the CAD (required for currentViewOnly / center); a 3D view places it model-wide. Default: a plan of placementLevel, else the active view"
    ),
  currentViewOnly: z
    .boolean()
    .optional()
    .default(false)
    .describe("Visible only in the target view (view-specific, not possible in 3D). Default false = visible in all views"),
  units: z
    .enum(["auto", "mm", "cm", "dm", "m", "inch", "ft", "custom"])
    .optional()
    .default("auto")
    .describe("Import units; auto reads them from the file"),
  customScale: z.number().positive().finite().optional().describe("Scale factor when units is custom"),
  positioning: z
    .enum(["origin", "center", "shared"])
    .optional()
    .default("origin")
    .describe("Auto - Origin to Internal Origin (default), Auto - Center to Center (uses the view), or By Shared Coordinates"),
  insertionPoint: point3Schema.optional().describe("Origin positioning only: model point (mm) for the CAD origin"),
  placementLevel: z
    .union([z.string().min(1).max(256), elementIdSchema])
    .optional()
    .describe("Level name or id to place on (Base Level); without viewId a plan view of this level is used"),
  baseOffset: z.number().finite().optional().describe("Base Offset from the level (mm), model-wide placement only"),
  colorMode: z
    .enum(["preserve", "invert", "blackAndWhite"])
    .optional()
    .default("preserve")
    .describe("Layer colours: preserve, invert, or black and white"),
  layerSelection: z
    .enum(["all", "visible", "specified"])
    .optional()
    .default("all")
    .describe("Layers to bring in: all, only visible ones, or the layerNames list"),
  layerNames: z.array(z.string().min(1).max(256)).min(1).max(500).optional().describe("Layers for layerSelection specified"),
  hideLayers: layerPatternsSchema.optional().describe("Layers (wildcards) to hide in the target view after placement"),
  correctLinesOffAxis: z
    .boolean()
    .optional()
    .default(true)
    .describe("Correct lines that are slightly off axis (default true)"),
  orientToView: z.boolean().optional().default(false).describe("Orient to the target view (model-wide placement only)"),
  pin: z.boolean().optional().default(true).describe("Pin the instance after placement (default true)"),
};

const linkCadSchema = z.object(linkCadShape);
export type LinkCadArgs = z.infer<typeof linkCadSchema>;

/** Cross-field checks the Zod shape cannot express; returns an error message or null. */
export function linkCadProblem(args: LinkCadArgs): string | null {
  if (args.layerSelection === "specified" && !args.layerNames?.length) return "layerSelection 'specified' needs layerNames.";
  if (args.units === "custom" && args.customScale === undefined) return "units 'custom' needs customScale.";
  if (args.currentViewOnly && args.viewId === undefined && args.placementLevel !== undefined)
    return "currentViewOnly places into a view; give viewId instead of placementLevel.";
  if (args.insertionPoint && args.positioning !== "origin") return "insertionPoint only applies to positioning 'origin'.";
  return null;
}

export function registerLinkCadTool(server: McpServer) {
  server.tool(
    "link_cad",
    "Link or import a DWG/DXF file (Insert > Link CAD / Import CAD) with the dialog options: target view or all views, current view only, units, positioning (origin / center / shared), placement level and offset, colour mode, layer selection, correct lines slightly off axis, pin. " +
      "Returns the ImportInstance id, the CADLinkType id, the instance transform (origin mm, rotation) and the layer list. Read the drawing with get_cad_geometry; convert it to native lines with trace_cad_import. One undo step.",
    linkCadShape,
    async (args) => {
      const problem = linkCadProblem(args);
      if (problem) return cadError("link_cad", problem);
      return sendDocumentationCommand("link_cad", args, 180000);
    }
  );
}
