import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point3Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

/** One text-note leader; points are millimetres in the same coordinates as the note location. */
export const textNoteLeaderSchema = z
  .object({
    end: point3Schema.describe("Arrowhead point on the geometry being labelled (mm)"),
    elbow: point3Schema
      .optional()
      .describe(
        "Shoulder/elbow point (mm), Straight leaders only: the leader runs text -> elbow -> end. Omit for a horizontal shoulder level with the first text line"
      ),
    side: z
      .enum(["Left", "Right"])
      .optional()
      .describe("Side of the text the leader leaves from (default: the side facing 'end')"),
    shape: z.enum(["Straight", "Arc"]).optional().describe("Leader shape (default Straight)"),
  })
  .strict();

export const textNoteLeadersSchema = z.array(textNoteLeaderSchema).min(1).max(50);

export const leaderAttachmentSchema = z
  .enum(["TopLine", "Midpoint", "BottomLine"])
  .describe("Text line the leaders attach to on that side");

export const textNoteFormatSchema = z
  .object({
    bold: z.boolean().optional(),
    italic: z.boolean().optional(),
    underline: z.boolean().optional(),
    allCaps: z.boolean().optional().describe("Display the whole note in capitals"),
    list: z
      .enum(["None", "Bullet", "ArabicNumbers", "LowerCaseLetters", "UpperCaseLetters"])
      .optional()
      .describe("List style per paragraph (paragraphs after headingLines when that is set)"),
    headingLines: z
      .number()
      .int()
      .min(0)
      .max(100)
      .optional()
      .describe("Bold + underline the first N paragraphs, e.g. 1 for a 'NOTES:' heading"),
  })
  .strict();

export function registerCreateTextNoteTool(server: McpServer) {
  server.tool(
    "create_text_note",
    "Create text notes, optionally with leaders and formatting, in a view or on a sheet. Location is the top-left of the text box (for Left alignment) in model millimetres for model views and in sheet millimetres for sheets and drafting views. Width is the paper width in millimetres; omit it for a single unwrapped line. Separate paragraphs (list items) with line breaks. " +
      "Leaders: each leader goes from the text's Left or Right side (default: the side facing 'end') to 'end' (the arrowhead on the geometry, mm, same coordinates as location). For the typical-detail style (horizontal shoulder then angled leader) omit 'elbow' and a short horizontal shoulder level with the first text line is created; or give 'elbow' explicitly. Arc leaders ignore elbow. " +
      "leaderLeftAttachment/leaderRightAttachment pick the text line the leader attaches to. format applies bold/italic/underline/allCaps/list to the whole note; headingLines bolds+underlines the first N paragraphs and the list then numbers only the rest (NOTES blocks). Returns textNoteId and leaderCount per note.",
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
            leaders: textNoteLeadersSchema.optional().describe("Leaders to add to this note"),
            leaderLeftAttachment: leaderAttachmentSchema.optional(),
            leaderRightAttachment: leaderAttachmentSchema.optional(),
            format: textNoteFormatSchema.optional().describe("Whole-note text formatting"),
          })
        )
        .min(1)
        .max(500),
    },
    async (args) => sendDocumentationCommand("create_text_note", args)
  );
}
