import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export const ANNOTATION_KINDS = [
  "detailLines",
  "textNotes",
  "dimensions",
  "filledRegions",
  "detailComponents",
  "other",
] as const;

export function registerGetViewAnnotationsTool(server: McpServer) {
  server.tool(
    "get_view_annotations",
    "Read the view-specific 2D elements of a view or drafting view: detail lines/arcs (geometry, line style), text notes (text, location, type, width), dimensions (value, segments, referenced element ids), filled regions (type, boundary loops) and detail components. Coordinates are millimetres in the same model coordinates create_detail_lines and create_text_note accept, so results can be fed to modify_annotations. Results are ordered by id and paged with offset/limit.",
    {
      viewId: elementIdSchema.optional().describe("View to read (default active view)"),
      kinds: z
        .array(z.enum(ANNOTATION_KINDS))
        .min(1)
        .optional()
        .describe("Only these kinds of element (default all)"),
      limit: z.number().int().min(1).max(5000).optional().default(500),
      offset: z.number().int().min(0).optional().default(0),
    },
    async (args) => sendDocumentationCommand("get_view_annotations", args)
  );
}
