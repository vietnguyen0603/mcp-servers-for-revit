import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, parameterValuesSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerCreateSheetTool(server: McpServer) {
  server.tool(
    "create_sheet",
    "Create Revit sheets with a title block, sheet number, sheet name, parameter values and additional revisions. The title block is chosen by titleBlockTypeId, by family/type name, or defaults to the first loaded title block type (see list_sheets). Parameter values are written to the sheet, or to its title block when the sheet has no such parameter; numeric parameters use project display units.",
    {
      sheets: z
        .array(
          z.object({
            number: z.string().min(1).max(256).optional().describe("Sheet number; must be unique"),
            name: z.string().min(1).max(256).optional().describe("Sheet name"),
            titleBlockTypeId: elementIdSchema.optional(),
            titleBlockFamilyName: z.string().min(1).max(256).optional(),
            titleBlockTypeName: z.string().min(1).max(256).optional(),
            noTitleBlock: z.boolean().optional().describe("Create the sheet without a title block"),
            parameters: parameterValuesSchema.optional().describe("Parameter name → value"),
            revisionIds: z.array(elementIdSchema).max(100).optional().describe("Revisions to add to the sheet"),
          })
        )
        .min(1)
        .max(200),
    },
    async (args) => sendDocumentationCommand("create_sheet", args)
  );
}
