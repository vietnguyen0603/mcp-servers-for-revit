import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point2Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export const tagElementsShape = {
  viewId: elementIdSchema.optional().describe("View to tag in (default active view)"),
  elementIds: z.array(elementIdSchema).max(5000).optional().describe("Specific elements to tag"),
  categories: z
    .array(z.string().min(1).max(256))
    .max(32)
    .optional()
    .describe("Categories to tag when elementIds is omitted, e.g. OST_StructuralFraming or 'Structural Columns'"),
  tagTypeId: elementIdSchema.optional().describe("Tag family type to use (probe_tag_types shows what each tag type displays)"),
  tagTypeName: z
    .string()
    .min(1)
    .max(512)
    .optional()
    .describe('Tag type by name instead of tagTypeId: "Family: Type" (e.g. "M_Structural Framing Tag: Standard"), a type name or a family name'),
  untaggedOnly: z.boolean().optional().default(true).describe("Skip elements already tagged in the view"),
  addLeader: z.boolean().optional().default(false).describe("Create tags with a leader (ignored when leader is given)"),
  placement: z
    .enum(["center", "above", "below", "left", "right", "topRight", "topLeft", "bottomRight", "bottomLeft"])
    .optional()
    .describe(
      "Drawing-style placement preset, measured from the tag's real size: the tag box lands relative to the element's box in the view with a gap of offsetPaperMm, e.g. topRight for columns / footings. Linear elements (beams, walls) with center/above/below: centred on the element, above/below = beside it along its normal (outside half the beam/wall width); combine with orientation:'Model' to align the text with the element (kept readable). offset / offsetAlongNormalMm are added on top. Omit for the old behaviour (tag head at the location point / curve midpoint)"
    ),
  offsetPaperMm: z
    .number()
    .min(0)
    .max(100)
    .optional()
    .describe("Gap between element and tag for placement, in printed mm (multiplied by the view scale; default 2)"),
  leader: z
    .enum(["auto", "none", "always"])
    .optional()
    .describe(
      "Leader mode (overrides addLeader): none, always, or auto = add a leader only when avoidOverlaps had to move the tag more than leaderThresholdPaperMm from its intended position"
    ),
  leaderThresholdPaperMm: z
    .number()
    .positive()
    .max(200)
    .optional()
    .describe("leader:'auto' threshold in printed mm (default 5)"),
  orientation: z
    .enum(["Horizontal", "Vertical", "Model"])
    .optional()
    .default("Horizontal")
    .describe(
      "Horizontal (default), Vertical, or Model: the tag is rotated to run along a linear element (beam, wall), kept readable (never upside down); point elements follow their hand orientation. Model needs Revit 2022+; older versions fall back to Horizontal or Vertical, whichever is closer"
    ),
  offset: point2Schema.optional().describe("Tag head offset from the element anchor, in model mm"),
  offsetAlongNormalMm: z
    .number()
    .finite()
    .min(-100000)
    .max(100000)
    .optional()
    .describe(
      "Linear elements only: move the tag head this many model mm perpendicular to the element in the view, e.g. 400 puts beam tags beside the beam instead of on it (also on inclined beams). Positive = the side the text top faces (above a horizontal beam, left of a vertical one); added to offset"
    ),
  avoidOverlaps: z
    .boolean()
    .optional()
    .default(false)
    .describe(
      "After placing each tag, if its box overlaps an existing tag in the view or one placed earlier in this run, shift it in steps perpendicular to and along the element until it is free (up to maxShiftTries positions); tags that stay overlapping are reported"
    ),
  maxShiftTries: z
    .number()
    .int()
    .min(1)
    .max(40)
    .optional()
    .default(8)
    .describe("Positions tried per tag with avoidOverlaps (default 8)"),
  shiftStepMm: z
    .number()
    .positive()
    .max(100000)
    .optional()
    .describe("Shift step in model mm for avoidOverlaps (default: the tag's own size in that direction plus 10%)"),
  maxTags: z
    .number()
    .int()
    .min(1)
    .max(5000)
    .optional()
    .default(500)
    .describe("Refuse to run if more targets than this match"),
};

export function registerTagElementsTool(server: McpServer) {
  server.tool(
    "tag_elements",
    "Tag elements of any taggable category (beams, columns, doors, windows, rooms, ...) in one view. Targets are the given elementIds, or every element of the given categories visible in the view. By default elements already tagged in the view are skipped. Uses tagTypeId when given, otherwise the category's default tag; rooms receive room tags. " +
      "Tags are placed at the element's location point or curve midpoint plus an optional offset; offsetAlongNormalMm moves them beside linear elements, orientation:'Model' aligns them with the element, avoidOverlaps shifts tags that collide with other tags and reports the remaining overlaps. " +
      "For drawing-like results use placement presets in paper mm (e.g. placement:'topRight' for columns, placement:'above' + orientation:'Model' for beams, offsetPaperMm:2) with leader:'auto' so only tags pushed away by avoidOverlaps get a leader; tagTypeName picks the tag type by 'Family: Type'. " +
      "To show composed text such as 'B12-300x450', fill a text parameter with set_parameters (expression) and use a tag type that displays it (probe_tag_types).",
    tagElementsShape,
    async (args) => {
      if (!args.elementIds?.length && !args.categories?.length) {
        return {
          content: [{ type: "text" as const, text: "tag_elements requires elementIds or categories." }],
          isError: true,
        };
      }
      return sendDocumentationCommand("tag_elements", args, 300000);
    }
  );
}
