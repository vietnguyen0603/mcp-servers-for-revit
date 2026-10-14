import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { point3LengthSchema, positiveLengthSchema } from "../utils/lengthInput.js";

/** One text-note leader; points are millimetres in the same coordinates as the note location. */
export const textNoteLeaderSchema = z
  .object({
    end: point3LengthSchema.describe("Arrowhead point on the geometry being labelled (mm)"),
    elbow: point3LengthSchema
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
      "leaderLeftAttachment/leaderRightAttachment pick the text line the leader attaches to. format applies bold/italic/underline/allCaps/list to the whole note; headingLines bolds+underlines the first N paragraphs and the list then numbers only the rest (NOTES blocks). Pick the text type by textNoteTypeId or textNoteTypeName (list them with list_drafting_types). Points accept millimetre numbers or strings with units (2'-9\", 3/4\", 300mm). fitWidth sizes the box to the unwrapped text; measure returns extents {paper:{width,height}, model:{width,height}, bbox, box corners, lineCount, wrapped} (see also measure_text_notes). Returns textNoteId and leaderCount per note.",
    {
      notes: z
        .array(
          z.object({
            text: z.string().min(1).max(4096),
            location: point3LengthSchema.describe("Insertion point (mm)"),
            viewId: elementIdSchema.optional().describe("View or sheet to place the note in (default active view)"),
            textNoteTypeId: elementIdSchema.optional().describe("Text type id (default project text type); wins over textNoteTypeName"),
            textNoteTypeName: z
              .string()
              .min(1)
              .max(256)
              .optional()
              .describe(
                "Text type name, e.g. '1/8\" Arial -Arrow': exact case-insensitive match first, then a unique partial match (see list_drafting_types)"
              ),
            width: positiveLengthSchema(2000)
              .optional()
              .describe("Wrap width on paper (mm, or a unit string such as 3\")"),
            fitWidth: z
              .boolean()
              .optional()
              .describe(
                "Set the wrap width to the text's own unwrapped width so no line wraps (the box hugs the longest line); ignores width"
              ),
            measure: z
              .boolean()
              .optional()
              .describe("Return the measured extents (paper and model mm, bbox, line count, wrapped) after regenerating"),
            rotationDegrees: z
              .number()
              .finite()
              .optional()
              .describe("Counter-clockwise rotation of the text in the view plane (degrees), e.g. 90 for vertical text"),
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
