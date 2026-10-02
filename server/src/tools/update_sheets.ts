import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, parameterValuesSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerUpdateSheetsTool(server: McpServer) {
  server.tool(
    "update_sheets",
    "Update existing Revit sheets, found by sheetId or sheetNumber: renumber (fails if the number is taken), rename, write parameter values (to the sheet, or its title block when the sheet has no such parameter; numeric values use project display units) and add or remove additional revisions. Revisions that come from revision clouds on the sheet cannot be removed and are reported as warnings.",
    {
      sheets: z
        .array(
          z
            .object({
              sheetId: elementIdSchema.optional(),
              sheetNumber: z.string().min(1).max(256).optional().describe("Current sheet number (if sheetId is not given)"),
              newNumber: z.string().min(1).max(256).optional(),
              newName: z.string().min(1).max(256).optional(),
              parameters: parameterValuesSchema.optional().describe("Parameter name → value"),
              addRevisionIds: z.array(elementIdSchema).max(100).optional(),
              removeRevisionIds: z.array(elementIdSchema).max(100).optional(),
            })
            .refine((sheet) => sheet.sheetId !== undefined || sheet.sheetNumber !== undefined, {
              message: "Each sheet requires sheetId or sheetNumber",
            })
        )
        .min(1)
        .max(500),
    },
    async (args) => sendDocumentationCommand("update_sheets", args)
  );
}
