import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { absoluteFolderSchema } from "../utils/exportSchemas.js";

export function registerExportViewImageTool(server: McpServer) {
  server.tool(
    "export_view_image",
    "Export Revit views or sheets to image files (PNG, JPEG, BMP or TIFF) in a local folder, which is created if missing. Defaults to the active view. Revit appends the view type and name to fileNamePrefix; the files actually written are returned.",
    {
      viewIds: z.array(elementIdSchema).max(500).optional().describe("Views or sheets to export (default active view)"),
      folder: absoluteFolderSchema.describe("Absolute output folder, e.g. C:\\Exports\\Images"),
      fileNamePrefix: z.string().min(1).max(120).optional().describe("File name prefix (default project title)"),
      format: z.enum(["PNG", "JPEG", "BMP", "TIFF"]).optional().default("PNG"),
      pixelSize: z.number().int().min(64).max(15000).optional().default(2048).describe("Image size in pixels along fitDirection"),
      fitDirection: z.enum(["Horizontal", "Vertical"]).optional().default("Horizontal"),
    },
    async (args) => sendDocumentationCommand("export_view_image", args)
  );
}
