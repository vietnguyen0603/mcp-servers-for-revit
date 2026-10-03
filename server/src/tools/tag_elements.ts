import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point2Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerTagElementsTool(server: McpServer) {
  server.tool(
    "tag_elements",
    "Tag elements of any taggable category (beams, columns, doors, windows, rooms, ...) in one view. Targets are the given elementIds, or every element of the given categories visible in the view. By default elements already tagged in the view are skipped. Uses tagTypeId when given, otherwise the category's default tag; rooms receive room tags. Tags are placed at the element's location point or curve midpoint plus an optional offset.",
    {
      viewId: elementIdSchema.optional().describe("View to tag in (default active view)"),
      elementIds: z.array(elementIdSchema).max(5000).optional().describe("Specific elements to tag"),
      categories: z
        .array(z.string().min(1).max(256))
        .max(32)
        .optional()
        .describe("Categories to tag when elementIds is omitted, e.g. OST_StructuralFraming or 'Structural Columns'"),
      tagTypeId: elementIdSchema.optional().describe("Tag family type to use"),
      untaggedOnly: z.boolean().optional().default(true).describe("Skip elements already tagged in the view"),
      addLeader: z.boolean().optional().default(false),
      orientation: z.enum(["Horizontal", "Vertical"]).optional().default("Horizontal"),
      offset: point2Schema.optional().describe("Tag head offset from the element anchor, in model mm"),
      maxTags: z
        .number()
        .int()
        .min(1)
        .max(5000)
        .optional()
        .default(500)
        .describe("Refuse to run if more targets than this match"),
    },
    async (args) => {
      if (!args.elementIds?.length && !args.categories?.length) {
        return {
          content: [{ type: "text" as const, text: "tag_elements requires elementIds or categories." }],
          isError: true,
        };
      }
      return sendDocumentationCommand("tag_elements", args);
    }
  );
}
