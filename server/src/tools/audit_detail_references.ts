import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { detailReferenceOptionsShape } from "../utils/detailLibrarySchemas.js";

export function registerAuditDetailReferencesTool(server: McpServer) {
  server.tool(
    "audit_detail_references",
    "Audit manual detail-reference bubbles: view-specific family instances whose family name contains `familyNameContains` (default 'Section Cut') and that carry the `detailNumberParam`/`sheetNumberParam` text parameters (default 'Detail Number'/'Sheet Number'). Each bubble is `ok` (sheet + detail number match a placed viewport; target view returned, reason 'selfReference' when it points at its own view), `placeholder` (either value is in `placeholders`, default '', '-', '--', 'X', 'XX', 'S-000') or `dead` (reason 'sheetNotFound' or 'detailNotOnSheet'). Also lists live View References (OST_ReferenceViewer) with their target view and its sheet/detail number (status ok | unplaced | missing). Scope with `viewIds` (host views) or `sheetNumbers` (sheets the host view is placed on). Rows exclude `ok` bubbles unless `includeOk`. Read-only.",
    {
      viewIds: z.array(elementIdSchema).max(5000).optional().describe("Only bubbles hosted in these views"),
      sheetNumbers: z
        .array(z.string().min(1).max(64))
        .max(500)
        .optional()
        .describe("Only bubbles whose host view is placed on these sheets"),
      includeOk: z.boolean().optional().default(false).describe("Also return rows for resolved bubbles"),
      ...detailReferenceOptionsShape,
      placeholders: z
        .array(z.string().max(64))
        .max(50)
        .optional()
        .describe("Values treated as placeholders (case-insensitive, trimmed)"),
      limit: z.number().int().min(1).max(5000).optional().default(500),
      offset: z.number().int().min(0).optional().default(0),
    },
    async (args) => sendDocumentationCommand("audit_detail_references", args)
  );
}
