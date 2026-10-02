import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerFindTagOverlapsTool(server: McpServer) {
  server.tool(
    "find_tag_overlaps",
    "Report overlapping annotations in one view, read-only. Compares the view-plane extents (bounding boxes) of tags, room tags and text notes by default, or of the given annotation categories, and optionally checks them against model elements. Returns overlapping pairs sorted by overlap area in paper square millimetres. Nothing is moved.",
    {
      viewId: elementIdSchema.optional().describe("View to check (default active view)"),
      categories: z
        .array(z.string().min(1).max(256))
        .max(32)
        .optional()
        .describe(
          "Annotation categories to compare, e.g. OST_StructuralFramingTags, OST_Dimensions, 'Text Notes' (default: all element tags, room tags and text notes)"
        ),
      toleranceMm: z
        .number()
        .min(0)
        .max(100)
        .optional()
        .default(0)
        .describe("Clearance on paper in mm; extents closer than this count as overlapping"),
      includeModelElements: z
        .boolean()
        .optional()
        .default(false)
        .describe("Also report annotations overlapping model elements of modelCategories"),
      modelCategories: z
        .array(z.string().min(1).max(256))
        .max(32)
        .optional()
        .describe("Model categories to check against when includeModelElements is true, e.g. OST_StructuralColumns"),
      maxPairs: z.number().int().min(1).max(10000).optional().default(500).describe("Maximum pairs to return"),
    },
    async (args) => {
      if (args.includeModelElements && !args.modelCategories?.length) {
        return {
          content: [{ type: "text" as const, text: "find_tag_overlaps: modelCategories is required when includeModelElements is true." }],
          isError: true,
        };
      }
      return sendDocumentationCommand("find_tag_overlaps", args);
    }
  );
}
