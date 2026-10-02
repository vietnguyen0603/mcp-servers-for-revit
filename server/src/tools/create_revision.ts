import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerCreateRevisionTool(server: McpServer) {
  server.tool(
    "create_revision",
    "Create Revit revisions with description, date, issued by/to, visibility and numbering sequence (by name, Revit 2022+). Marking a revision issued is applied last because issued revisions are locked.",
    {
      revisions: z
        .array(
          z.object({
            description: z.string().max(1024).describe("Revision description"),
            date: z.string().max(256).optional().describe("Revision date text, e.g. 2026-10-02"),
            issuedBy: z.string().max(256).optional(),
            issuedTo: z.string().max(256).optional(),
            issued: z.boolean().optional().describe("Mark the revision as issued (locks it)"),
            visibility: z.enum(["Hidden", "TagVisible", "CloudAndTagVisible"]).optional(),
            numberingSequenceName: z
              .string()
              .min(1)
              .max(256)
              .optional()
              .describe("Revision numbering sequence name (Revit 2022+)"),
          })
        )
        .min(1)
        .max(100),
    },
    async (args) => sendDocumentationCommand("create_revision", args)
  );
}
