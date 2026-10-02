import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerListRevisionsTool(server: McpServer) {
  server.tool(
    "list_revisions",
    "List all revisions in sequence order with id, sequence and revision number, date, description, issued state, issued by/to, visibility, numbering sequence (Revit 2022+) and the sheet numbers each revision appears on. Also returns the revision numbering sequences (Revit 2022+).",
    {},
    async (args) => sendDocumentationCommand("list_revisions", args)
  );
}
