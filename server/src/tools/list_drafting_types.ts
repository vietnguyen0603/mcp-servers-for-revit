import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

export const DRAFTING_TYPE_KINDS = [
  "textNoteTypes",
  "dimensionTypes",
  "filledRegionTypes",
  "lineStyles",
  "viewportTypes",
  "titleBlockTypes",
  "detailGroupTypes",
] as const;

export function registerListDraftingTypesTool(server: McpServer) {
  server.tool(
    "list_drafting_types",
    "List the drafting/annotation types in the model, grouped by kind, so exact names and ids can be passed to the drawing tools: textNoteTypes (id, name, size in mm and inches, font, leader arrowhead) for create_text_note textNoteTypeName/textNoteTypeId; dimensionTypes (id, name, style Linear/Angular/Radial/...) for create_dimensions; filledRegionTypes (id, name, isMasking) for create_filled_region; lineStyles (id, name, weight, pattern) for create_detail_lines; viewportTypes (id, name) for place_viewport/update_viewports; titleBlockTypes (id, family, type) for create_sheet; detailGroupTypes as a count only (use list_detail_groups for the list). " +
      "Call it before drafting to check what the office standards provide (e.g. which Tick dimension or 1/8\" text type exists). Filter with kinds and nameContains to keep the result small. Read-only.",
    {
      kinds: z
        .array(z.enum(DRAFTING_TYPE_KINDS))
        .min(1)
        .max(DRAFTING_TYPE_KINDS.length)
        .optional()
        .describe("Kinds to return (default all)"),
      nameContains: z
        .string()
        .min(1)
        .max(256)
        .optional()
        .describe("Case-insensitive filter on the name (title blocks: family or type name)"),
    },
    async (args) => sendDocumentationCommand("list_drafting_types", args)
  );
}
