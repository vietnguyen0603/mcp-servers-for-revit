import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema } from "../utils/documentationSchemas.js";
import {
  STANDARDS_SECTIONS,
  errorText,
  renderStandardsMarkdown,
  requestRevit,
  textResult,
} from "../utils/draftingLibrary.js";

export function registerGetDraftingStandardsTool(server: McpServer) {
  server.tool(
    "get_drafting_standards",
    "Harvest the drafting standards actually USED in the model's drafting views, ranked by use, so details are drawn with the office's own styles/types/families: scales (views per 1:N), lineStyles (uses, weight, color, pattern), textTypes (uses, size, font, bold, leader arrowhead; text-with-leader count), dimensionTypes (uses, style, common value overrides), filledRegionTypes (uses, masking, CAD-derived '*.dwg' names), detailComponents and annotationSymbols (family : type, uses, point/line placement or leaders + editable parameters, role), roles (top 5 family types per role: sectionCut, subtitle, weld, elevationMarker, breakLine, rebar, fastener, framing, steelShape, gridHead), detailGroups, viewportTypes (all viewports), titleBlocks and viewNames (naming-pattern matches, prefixes, samples). " +
      "format 'markdown' (default) returns the compact snapshot layout of the revit-detail-standards skill (save it as reference/snapshot-<model>.md); 'json' returns the raw sections. Pick sections and `top` to keep the output small; includeUnused adds zero-use types. Scope with viewIds / sheetNumbers / viewNameContains (default all drafting views). Read-only.",
    {
      sections: z
        .array(z.enum(STANDARDS_SECTIONS))
        .min(1)
        .max(STANDARDS_SECTIONS.length)
        .optional()
        .describe("Sections to return (default all)"),
      format: z.enum(["markdown", "json"]).optional().default("markdown"),
      top: z.number().int().min(1).max(500).optional().default(30).describe("Rows per ranked section"),
      includeUnused: z
        .boolean()
        .optional()
        .default(false)
        .describe("Also list line styles, text/dimension/fill and viewport types with 0 uses"),
      viewIds: z.array(elementIdSchema).max(5000).optional().describe("Harvest these views instead of all drafting views"),
      sheetNumbers: z
        .array(z.string().min(1).max(64))
        .max(500)
        .optional()
        .describe("Only views placed on these sheets"),
      viewNameContains: z.string().min(1).max(256).optional().describe("Only views whose name contains this (case-insensitive)"),
      namePattern: z
        .string()
        .min(1)
        .max(500)
        .optional()
        .describe("View naming regex (.NET, case-insensitive) for viewNames (default '^[A-Z]{2,4}-[A-Z]{1,5}-\\d+[A-Z]?(-[A-Z]+)?_')"),
      viewNameSamples: z.number().int().min(0).max(200).optional().default(16),
    },
    async (args) => {
      const { format, ...params } = args;
      try {
        const outcome = await requestRevit<Record<string, any>>("get_drafting_standards", params);
        if (!outcome.success || !outcome.body) {
          return textResult(JSON.stringify(outcome.raw, null, 2), true);
        }
        return format === "json"
          ? textResult(JSON.stringify(outcome.raw, null, 2))
          : textResult(renderStandardsMarkdown(outcome.body));
      } catch (error) {
        return errorText("get_drafting_standards", error);
      }
    }
  );
}
