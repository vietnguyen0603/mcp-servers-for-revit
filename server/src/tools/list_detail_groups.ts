import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerListDetailGroupsTool(server: McpServer) {
  server.tool(
    "list_detail_groups",
    "List detail group types (typical symbols such as screws, wire mesh, piles) with id, name, instance count, member count and a sample placed instance and its view. Use the ids with modify_detail_groups to place more instances.",
    {
      nameContains: z.string().min(1).max(256).optional().describe("Case-insensitive filter on the type name"),
      limit: z.number().int().min(1).max(5000).optional().default(500),
      offset: z.number().int().min(0).optional().default(0),
    },
    async (args) => sendDocumentationCommand("list_detail_groups", args)
  );
}
