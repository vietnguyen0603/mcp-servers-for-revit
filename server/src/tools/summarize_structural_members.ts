import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

const textList = z.array(z.union([z.string().max(1024), z.number().finite()])).max(1000);

export const structuralMemberFilterSchema = z
  .object({
    elementIds: z.array(elementIdSchema).max(50000).optional().describe("Only these elements"),
    category: z.enum(["framing", "columns", "all"]).optional().describe("Default all"),
    comments: textList.optional().describe("Comments equal to one of these (case-insensitive), e.g. ['TG-1','GH-1']"),
    marks: textList.optional().describe("Mark equal to one of these"),
    familyName: z.string().min(1).max(256).optional(),
    typeName: z.string().min(1).max(256).optional(),
    level: z.string().min(1).max(256).optional().describe("Reference level (framing) or base level (columns) name"),
  })
  .strict()
  .default({})
  .describe("Every given criterion must match; empty selects all framing and columns");

export function registerSummarizeStructuralMembersTool(server: McpServer) {
  server.tool(
    "summarize_structural_members",
    "Read-only check of placed structural framing and columns: groups the members matching a filter by comments, mark, type, usage, level and/or category, and reports each group's count, bottom and top elevation ranges (bounding box, mm), total framing length (mm) and element ids. " +
      "Use it after create_beams / create_structural_columns / modify_structural_members to verify quantities, types, usage and that beam undersides meet post tops.",
    {
      filter: structuralMemberFilterSchema,
      groupBy: z
        .array(z.enum(["comments", "mark", "type", "usage", "level", "category"]))
        .min(1)
        .max(6)
        .default(["comments", "type"])
        .describe("Group key parts, joined with ' | '"),
      maxIds: z.number().int().min(0).max(5000).default(50).describe("Element ids listed per group"),
    },
    async (args) => sendDocumentationCommand("summarize_structural_members", args, 120000)
  );
}
