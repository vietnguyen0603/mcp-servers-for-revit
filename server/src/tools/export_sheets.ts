import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { absoluteFolderSchema } from "../utils/exportSchemas.js";

export function registerExportSheetsTool(server: McpServer) {
  server.tool(
    "export_sheets",
    "Export Revit sheets (by id or sheet number) or printable views to PDF or DWG files in a local folder, which is created if missing. Each item is exported to its own file named by fileNameTemplate ({number}, {name}, {viewType}); PDF can instead be combined into one file. PDF export requires Revit 2022 or later. Returns the files actually written per item.",
    {
      format: z.enum(["PDF", "DWG"]),
      sheetIds: z.array(elementIdSchema).max(1000).optional(),
      sheetNumbers: z.array(z.string().min(1).max(256)).max(1000).optional(),
      viewIds: z.array(elementIdSchema).max(1000).optional().describe("Printable views to export (non-sheet)"),
      folder: absoluteFolderSchema.describe("Absolute output folder, e.g. C:\\Exports\\Issue-01"),
      combine: z.boolean().optional().default(false).describe("PDF only: combine everything into one file"),
      combinedFileName: z.string().min(1).max(200).optional().describe("PDF only: name of the combined file"),
      fileNameTemplate: z
        .string()
        .min(1)
        .max(200)
        .optional()
        .default("{number} - {name}")
        .describe("File name per item; placeholders {number} (sheet number), {name}, {viewType}"),
      pdf: z
        .object({
          paperPlacement: z.enum(["Center", "LowerLeft"]).optional(),
          zoom: z
            .union([z.number().int().min(1).max(10000), z.literal("FitToPage")])
            .optional()
            .describe("Zoom percent, or FitToPage"),
          colorDepth: z.enum(["Color", "BlackLine", "GrayScale"]).optional(),
          rasterQuality: z.enum(["Low", "Medium", "High", "Presentation"]).optional(),
          hideCropBoundaries: z.boolean().optional(),
          hideScopeBoxes: z.boolean().optional(),
          hideUnreferencedViewTags: z.boolean().optional(),
        })
        .optional(),
      dwg: z
        .object({
          exportSetupName: z.string().min(1).max(256).optional().describe("Named DWG export setup in the project"),
          mergedViews: z.boolean().optional().describe("Merge views into the sheet DWG instead of xrefs"),
        })
        .optional(),
    },
    async (args) => {
      if (!args.sheetIds?.length && !args.sheetNumbers?.length && !args.viewIds?.length) {
        return {
          content: [{ type: "text" as const, text: "export_sheets requires sheetIds, sheetNumbers or viewIds." }],
          isError: true,
        };
      }
      return sendDocumentationCommand("export_sheets", args);
    }
  );
}
