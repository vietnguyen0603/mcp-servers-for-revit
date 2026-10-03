import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { graphicOverridesSchema } from "../utils/graphicOverrideSchema.js";

export function registerOverrideGraphicsTool(server: McpServer) {
  server.tool(
    "override_graphics",
    "Override graphics of specific elements and/or whole categories in one view (Visibility/Graphics): halftone, transparency, projection/cut line colour, weight and pattern, surface/cut fill colour and pattern, and visibility. A fill colour without a pattern uses solid fill. Set reset=true to clear the overrides and unhide the targets. Each target is applied independently; the call is one undo step.",
    {
      viewId: elementIdSchema.optional().describe("View to modify (default active view)"),
      elementIds: z.array(elementIdSchema).max(5000).optional().describe("Elements to override"),
      categories: z
        .array(z.string().min(1).max(256))
        .max(64)
        .optional()
        .describe("Categories to override, e.g. OST_StructuralFraming or 'Structural Columns'"),
      reset: z.boolean().optional().default(false).describe("Clear overrides and unhide instead of applying"),
      overrides: graphicOverridesSchema.optional(),
    },
    async (args) => {
      if (!args.elementIds?.length && !args.categories?.length) {
        return {
          content: [{ type: "text" as const, text: "override_graphics requires elementIds or categories." }],
          isError: true,
        };
      }
      if (!args.reset && (!args.overrides || Object.keys(args.overrides).length === 0)) {
        return {
          content: [{ type: "text" as const, text: "override_graphics requires overrides unless reset is true." }],
          isError: true,
        };
      }
      return sendDocumentationCommand("override_graphics", args);
    }
  );
}
