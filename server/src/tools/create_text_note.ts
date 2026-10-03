import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point3Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerCreateTextNoteTool(server: McpServer) {
  server.tool(
    "create_text_note",
    "Create text notes in a view or on a sheet. Location is in model millimetres for model views and in sheet millimetres for sheets and drafting views. Width is the paper width in millimetres; omit it for a single unwrapped line.",
    {
      notes: z
        .array(
          z.object({
            text: z.string().min(1).max(4096),
            location: point3Schema.describe("Insertion point (mm)"),
            viewId: elementIdSchema.optional().describe("View or sheet to place the note in (default active view)"),
            textNoteTypeId: elementIdSchema.optional().describe("Text type (default project text type)"),
            width: z.number().positive().max(2000).optional().describe("Wrap width on paper (mm)"),
            rotationDegrees: z.number().finite().optional(),
            horizontalAlignment: z.enum(["Left", "Center", "Right"]).optional(),
          })
        )
        .min(1)
        .max(500),
    },
    async (args) => sendDocumentationCommand("create_text_note", args)
  );
}
