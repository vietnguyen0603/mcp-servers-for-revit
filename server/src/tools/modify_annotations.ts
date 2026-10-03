import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import {
  elementIdSchema,
  parameterValuesSchema,
  point3Schema,
  sendDocumentationCommand,
} from "../utils/documentationSchemas.js";
import { leaderAttachmentSchema, textNoteLeadersSchema } from "./create_text_note.js";

const elementIds = z.array(elementIdSchema).min(1).max(1000).describe("Elements to change");

const operationSchema = z.discriminatedUnion("action", [
  z.object({ action: z.literal("move"), elementIds, delta: point3Schema.describe("Translation (mm)") }).strict(),
  z
    .object({ action: z.literal("copy"), elementIds, delta: point3Schema.describe("Offset of the copies (mm)") })
    .strict(),
  z
    .object({
      action: z.literal("rotate"),
      elementIds,
      center: point3Schema.describe("Rotation centre (mm)"),
      angleDeg: z.number().finite().describe("Counter-clockwise angle in the view plane"),
    })
    .strict(),
  z.object({ action: z.literal("delete"), elementIds }).strict(),
  z.object({ action: z.literal("setText"), elementIds, text: z.string().min(1).max(4096) }).strict(),
  z
    .object({
      action: z.literal("setLineStyle"),
      elementIds,
      lineStyle: z.string().min(1).max(256).describe("Lines subcategory, e.g. '<Thin Lines>'"),
    })
    .strict(),
  z
    .object({
      action: z.literal("setLine"),
      elementIds: z.array(elementIdSchema).length(1),
      start: point3Schema,
      end: point3Schema,
    })
    .strict()
    .describe("Move the endpoints of one straight detail line (mm)"),
  z
    .object({
      action: z.literal("setType"),
      elementIds,
      typeId: elementIdSchema.optional(),
      typeName: z.string().min(1).max(256).optional(),
    })
    .strict()
    .describe("Change the type of text notes, dimensions, filled regions or detail components"),
  z.object({ action: z.literal("setParameters"), elementIds, parameters: parameterValuesSchema }).strict(),
  z
    .object({
      action: z.literal("addLeaders"),
      elementIds,
      leaders: textNoteLeadersSchema,
      leaderLeftAttachment: leaderAttachmentSchema.optional(),
      leaderRightAttachment: leaderAttachmentSchema.optional(),
    })
    .strict()
    .describe("Text notes only: add leaders ({end, elbow?, side?, shape?} in mm, same as create_text_note)"),
  z
    .object({
      action: z.literal("setLeaders"),
      elementIds,
      leaders: textNoteLeadersSchema,
      leaderLeftAttachment: leaderAttachmentSchema.optional(),
      leaderRightAttachment: leaderAttachmentSchema.optional(),
    })
    .strict()
    .describe("Text notes only: replace all leaders with the given ones"),
  z.object({ action: z.literal("removeLeaders"), elementIds }).strict().describe("Text notes only: remove all leaders"),
]);

export function registerModifyAnnotationsTool(server: McpServer) {
  server.tool(
    "modify_annotations",
    "Edit 2D elements in views and drafting views: move, copy, rotate, delete, setText (text notes), setLineStyle (detail lines), setLine (endpoints of one detail line), setType (text/dimension/filled region/detail component types) and setParameters. Coordinates are millimetres; use get_view_annotations to find ids and current geometry. All operations are one undo step; each reports its own success.",
    {
      operations: z
        .array(operationSchema)
        .min(1)
        .max(500)
        .superRefine((ops, ctx) =>
          ops.forEach((op, i) => {
            if (op.action === "setType" && op.typeId === undefined && op.typeName === undefined) {
              ctx.addIssue({ code: z.ZodIssueCode.custom, path: [i], message: "setType needs typeId or typeName" });
            }
          })
        ),
    },
    async (args) => sendDocumentationCommand("modify_annotations", args)
  );
}
