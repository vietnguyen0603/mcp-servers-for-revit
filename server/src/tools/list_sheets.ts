import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerListSheetsTool(server: McpServer) {
  server.tool(
    "list_sheets",
    "List sheets in the current Revit project with number, name, title blocks, viewports (view and box centre in sheet millimetres) and placed schedules, plus every loaded title block type for use with create_sheet.",
    {
      numberContains: z
        .string()
        .max(256)
        .optional()
        .describe("Case-insensitive substring filter on sheet number or name"),
      includeContents: z
        .boolean()
        .optional()
        .default(true)
        .describe("Include title blocks, viewports and schedules on each sheet"),
      limit: z.number().int().min(1).max(5000).optional().default(500).describe("Maximum sheets to return"),
    },
    async (args) => sendDocumentationCommand("list_sheets", args)
  );
}
