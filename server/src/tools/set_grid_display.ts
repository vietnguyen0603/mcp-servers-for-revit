import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export const BUBBLE_SIDES = [
  "top",
  "bottom",
  "left",
  "right",
  "top-left",
  "top-right",
  "bottom-left",
  "bottom-right",
  "both",
  "none",
  "start",
  "end",
] as const;

const gridRef = z
  .union([z.string().min(1).max(64), elementIdSchema])
  .describe("Grid name (e.g. 'A', 'C.5') or element id");

const bubbleSide = z
  .enum(BUBBLE_SIDES)
  .describe(
    "Which end shows the bubble, resolved per grid from where its ends lie in the view: 'top'/'bottom' for vertical grids, 'left'/'right' for horizontal ones; " +
      "'top-left' = vertical grids at the top AND horizontal grids at the left (likewise top-right, bottom-left, bottom-right); 'both', 'none'; 'start'/'end' = End0/End1 of the grid curve"
  );

export const gridExtentsSchema = z
  .union([
    z.literal("clipToCrop"),
    z
      .object({
        offsetMm: z.number().finite().min(-100000).max(100000).optional().describe("Model mm inside the crop box (negative = beyond it)"),
        offsetPaperMm: z
          .number()
          .finite()
          .min(-500)
          .max(500)
          .optional()
          .describe("Printed mm inside the crop box (scaled by the view scale), e.g. 12 keeps the bubbles inside the crop"),
      })
      .strict()
      .refine((o) => (o.offsetMm === undefined) !== (o.offsetPaperMm === undefined), {
        message: "Give exactly one of offsetMm or offsetPaperMm",
      }),
  ])
  .describe("2D (view-specific) grid extents: 'clipToCrop' trims/extends each straight grid to the crop box edge; {offsetPaperMm} or {offsetMm} insets it");

export function registerSetGridDisplayTool(server: McpServer) {
  server.tool(
    "set_grid_display",
    "Fix how grids display in plan/section/elevation views: bubble ends, 2D extents and propagation. " +
      "Bubbles are resolved geometrically per grid in each view (End0/End1 order is arbitrary), e.g. bubbles:'top-left' puts vertical grid bubbles at the top and horizontal ones at the left; groups override per grid set, e.g. groups:[{grids:['1','2'], bubbles:'bottom'}]. " +
      "extents:'clipToCrop' or {offsetPaperMm:12} sets view-specific (2D) extents of straight grids to the crop region so every bubble sits at the drawing edge. " +
      "propagateToViews copies the 2D extents of the first view to parallel views (ids). Grids not visible in a view are skipped and listed under notVisible; grids parallel to the requested side are left unchanged. One undo step.",
    {
      views: z.array(elementIdSchema).min(1).max(200).describe("View ids to update (see list_views)"),
      grids: z.array(gridRef).min(1).max(1000).optional().describe("Grids to change (default: all grids visible in each view)"),
      bubbles: bubbleSide.optional(),
      groups: z
        .array(
          z
            .object({
              grids: z.array(gridRef).min(1).max(1000).describe("Grids in this group"),
              bubbles: bubbleSide,
            })
            .strict()
        )
        .max(50)
        .optional()
        .describe("Per-group bubble overrides (later groups win)"),
      extents: gridExtentsSchema.optional(),
      propagateToViews: z
        .array(elementIdSchema)
        .min(1)
        .max(500)
        .optional()
        .describe("Copy the 2D grid extents from the first view in 'views' to these parallel views"),
    },
    async (args) => {
      if (!args.bubbles && !args.groups?.length && !args.extents && !args.propagateToViews?.length) {
        return {
          content: [{ type: "text" as const, text: "set_grid_display failed: give bubbles, groups, extents or propagateToViews" }],
          isError: true,
        };
      }
      return sendDocumentationCommand("set_grid_display", args, 200000);
    }
  );
}
