import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point3Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

const elementIds = z.array(elementIdSchema).min(1).max(500).describe("View-specific elements copied together");

const linearSchema = z
  .object({
    elementIds,
    mode: z.literal("linear"),
    along: z
      .object({ start: point3Schema, end: point3Schema })
      .strict()
      .optional()
      .describe("Direction and length of the array (mm); offsets are measured from the source's current position"),
    direction: point3Schema.optional().describe("Direction vector when no length is needed (fit 'spacing')"),
    count: z.number().int().min(1).max(1000).optional().describe("Members (see includeOriginal)"),
    spacing: z.number().finite().positive().optional().describe("Step between members (mm)"),
    fit: z
      .enum(["spacing", "count", "fill"])
      .optional()
      .describe(
        "spacing = count members at spacing; count = count members spread over the along length; fill = as many members at spacing as fit in the along length. Default: fill with along+spacing, count with along+count, else spacing"
      ),
    includeOriginal: z
      .boolean()
      .optional()
      .default(true)
      .describe("The source is the first member and count includes it; false = count is the number of copies"),
  })
  .strict()
  .superRefine((item, ctx) => {
    const issue = (message: string) => ctx.addIssue({ code: z.ZodIssueCode.custom, message });
    if (!item.along === !item.direction) issue("Provide exactly one of along or direction");
    const fit =
      item.fit ??
      (item.along && item.spacing !== undefined && item.count === undefined
        ? "fill"
        : item.along && item.count !== undefined && item.spacing === undefined
          ? "count"
          : "spacing");
    if (fit === "spacing" && (item.spacing === undefined || item.count === undefined))
      issue("fit 'spacing' needs spacing and count");
    if (fit === "count" && (item.count === undefined || !item.along)) issue("fit 'count' needs count and along");
    if (fit === "fill" && (item.spacing === undefined || !item.along)) issue("fit 'fill' needs spacing and along");
  });

const pointsSchema = z
  .object({
    elementIds,
    mode: z.literal("points"),
    points: z.array(point3Schema).min(1).max(1000).describe("Target positions of the reference point (mm)"),
    reference: point3Schema
      .optional()
      .describe("Reference point of the source (default: first element's location point, else its bounding-box centre)"),
  })
  .strict();

const arraySchema = z.union([linearSchema, pointsSchema]);

export function registerArrayAnnotationsTool(server: McpServer) {
  server.tool(
    "array_annotations",
    "Repeat view-specific elements (detail components, detail lines, detail groups, text...) as plain unassociated copies: mode 'linear' at a spacing/count along a direction, e.g. rebar dots, nails @ 6\" o.c. or screws filling a length; mode 'points' copies the source so its reference point lands on each point. Millimetres. All arrays are one undo step; each item reports the new ids per step.",
    {
      arrays: z.array(arraySchema).min(1).max(200),
    },
    async (args) => sendDocumentationCommand("array_annotations", args)
  );
}
