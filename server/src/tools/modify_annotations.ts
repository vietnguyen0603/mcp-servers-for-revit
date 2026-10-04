import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import {
  elementIdSchema,
  parameterValuesSchema,
  point3Schema,
  sendDocumentationCommand,
} from "../utils/documentationSchemas.js";
import { leaderAttachmentSchema, textNoteLeadersSchema } from "./create_text_note.js";
import { dimensionSegmentsTextSchema, dimensionTextSchema } from "../utils/dimensionSchemas.js";

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
  z
    .object({
      action: z.literal("setDimensionText"),
      elementIds,
      text: dimensionTextSchema.optional(),
      segments: dimensionSegmentsTextSchema.optional(),
    })
    .strict()
    .describe(
      "Set dimension value override/prefix/suffix/above/below, text position (mm) and leader ('text' = all segments, 'segments' = by index)"
    ),
  z
    .object({
      action: z.literal("mirror"),
      elementIds,
      axis: z.object({ start: point3Schema, end: point3Schema }).strict().describe("Mirror axis in the view plane (mm)"),
      copy: z.boolean().optional().default(false).describe("Keep the originals and mirror copies"),
    })
    .strict()
    .describe("Mirror about an axis line in the view plane"),
  z
    .object({ action: z.literal("flip"), elementIds })
    .strict()
    .describe(
      "Flip detail components in place: line-based items reverse their line direction (a break line then masks the other side), point-based items flip hand/facing or mirror about their own vertical axis"
    ),
]);

export function registerModifyAnnotationsTool(server: McpServer) {
  server.tool(
    "modify_annotations",
    "Edit 2D elements in views and drafting views: move, copy, rotate, delete, setText (text notes), setLineStyle (detail lines), setLine (endpoints of one detail line), setType (text/dimension/filled region/detail component types), setParameters, setDimensionText (dimension override/prefix/suffix/above/below, text position and leader), addLeaders/setLeaders/removeLeaders (text notes), mirror (about an axis, optionally as copies) and flip (detail components; flipping a break line swaps its masked side). Coordinates are millimetres; use get_view_annotations to find ids and current geometry. All operations are one undo step; each reports its own success.",
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
            if (op.action === "setDimensionText" && !op.text && !op.segments) {
              ctx.addIssue({ code: z.ZodIssueCode.custom, path: [i], message: "setDimensionText needs text or segments" });
            }
          })
        ),
    },
    async (args) => sendDocumentationCommand("modify_annotations", args)
  );
}
