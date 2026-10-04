import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerGetDocumentInfoTool(server: McpServer) {
  server.tool(
    "get_document_info",
    "Get a compact overview of the active Revit document: title, file path, workshared flag, unsaved changes (isModified), Revit version, project length unit, active view (id, name, type), levels (id, name, elevation in mm) and the titles of all open documents. Read-only; call it first to check which model you are working in.",
    {},
    async () => sendDocumentationCommand("get_document_info", {})
  );
}
