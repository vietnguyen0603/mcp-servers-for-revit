import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import {
  elementIdSchema,
  parameterValuesSchema,
  point3Schema,
  sendDocumentationCommand,
} from "../utils/documentationSchemas.js";
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
      action: z.literal("setDimensionText"),
      elementIds,
      text: dimensionTextSchema.optional(),
      segments: dimensionSegmentsTextSchema.optional(),
    })
    .strict()
    .describe("Set dimension value override/prefix/suffix/above/below ('text' = all segments, 'segments' = by index)"),
]);

export function registerModifyAnnotationsTool(server: McpServer) {
  server.tool(
    "modify_annotations",
    "Edit 2D elements in views and drafting views: move, copy, rotate, delete, setText (text notes), setLineStyle (detail lines), setLine (endpoints of one detail line), setType (text/dimension/filled region/detail component types), setParameters and setDimensionText (dimension override/prefix/suffix/above/below). Coordinates are millimetres; use get_view_annotations to find ids and current geometry. All operations are one undo step; each reports its own success.",
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
