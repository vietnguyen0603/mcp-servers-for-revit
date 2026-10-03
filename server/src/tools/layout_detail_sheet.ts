import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point2Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

const marginSchema = z.number().finite().min(0).max(1000);

export function registerLayoutDetailSheetTool(server: McpServer) {
  server.tool(
    "layout_detail_sheet",
    "Lay out views (typically drafting details) on a sheet in a module grid. Each view is placed on the sheet (an existing viewport of that view on this sheet is reused; views on other sheets fail), measured from its viewport box plus title label, then packed first-fit in `order` into a columns x rows grid over `region`; details larger than a cell span several cells and are centred in their block. Without columns/rows the coarsest grid up to 8x6 that fits every view is chosen. `region` is in sheet mm; by default it is the title block bounding box minus `margins` (default 20 mm, right 110 mm to clear a vertical title strip). Viewports are then numbered from `startDetailNumber` in grid order (skipping numbers held by other viewports on the sheet) unless numbering=false. dryRun rolls everything back and only reports the placements.",
    {
      sheetId: elementIdSchema.describe("Target sheet"),
      viewIds: z.array(elementIdSchema).min(1).max(100).describe("Views to lay out, in packing order"),
      region: z
        .object({ min: point2Schema, max: point2Schema })
        .strict()
        .optional()
        .describe("Layout area on the sheet (mm)"),
      margins: z
        .object({
          left: marginSchema.optional(),
          right: marginSchema.optional(),
          top: marginSchema.optional(),
          bottom: marginSchema.optional(),
        })
        .strict()
        .optional()
        .describe("Insets from the title block bounding box when region is omitted (mm)"),
      columns: z.number().int().min(1).max(20).optional(),
      rows: z.number().int().min(1).max(20).optional(),
      order: z.enum(["rightToLeftTopDown", "leftToRightTopDown"]).optional().default("rightToLeftTopDown"),
      gap: z.number().finite().min(0).max(200).optional().describe("Minimum clearance between details (mm, default 10)"),
      startDetailNumber: z.number().int().min(1).max(9999).optional().default(1),
      numbering: z.boolean().optional().default(true).describe("Renumber viewports in grid order"),
      viewportTypeId: elementIdSchema.optional().describe("Viewport type to apply before measuring"),
      dryRun: z.boolean().optional().default(false),
    },
    async (args) => sendDocumentationCommand("layout_detail_sheet", args)
  );
}
