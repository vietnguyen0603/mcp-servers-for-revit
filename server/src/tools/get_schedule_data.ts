import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerGetScheduleDataTool(server: McpServer) {
  server.tool(
    "get_schedule_data",
    "Read a Revit schedule as displayed: column headings plus the body rows as cell text (the first body rows are usually the headings). Without scheduleId or scheduleName, lists all schedules in the project.",
    {
      scheduleId: elementIdSchema.optional(),
      scheduleName: z.string().min(1).max(256).optional().describe("Exact schedule name (case-insensitive)"),
      maxRows: z.number().int().min(1).max(10000).optional().default(500),
    },
    async (args) => sendDocumentationCommand("get_schedule_data", args)
  );
}
