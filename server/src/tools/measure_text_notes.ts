import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerMeasureTextNotesTool(server: McpServer) {
  server.tool(
    "measure_text_notes",
    "Measure text notes after regenerating: per note the paper size (mm, as printed) and model size (paper x view scale; sheets 1:1) of the text box, its axis-aligned bounding box in the view (mm, includes leaders), the rotated box corners, the width setting and minimum allowed width (paper mm), whether wrapping is active, the line count, whether any line wraps (wrapped) and the unwrapped width (paper mm, the width fitWidth would use). Give elementIds, or viewId (default active view) to measure every text note in that view (up to limit). Read-only: the unwrapped measurement uses a temporary note in a transaction that is rolled back.",
    {
      elementIds: z.array(elementIdSchema).min(1).max(1000).optional().describe("Text notes to measure"),
      viewId: elementIdSchema
        .optional()
        .describe("Without elementIds: measure the text notes of this view (default active view); with elementIds: only those in this view"),
      limit: z.number().int().min(1).max(1000).optional().describe("Without elementIds: at most this many notes (default 500)"),
    },
    async (args) => sendDocumentationCommand("measure_text_notes", args)
  );
}
