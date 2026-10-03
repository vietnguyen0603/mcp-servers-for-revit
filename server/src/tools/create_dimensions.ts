import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point3Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { dimensionSegmentsTextSchema, dimensionTextSchema } from "../utils/dimensionSchemas.js";

const referenceSchema = z
  .object({
    elementId: elementIdSchema.describe("Detail line/arc, model line, wall, grid, detail component..."),
    end: z
      .enum(["curve", "start", "end", "center", "left", "right", "front", "back", "top", "bottom"])
      .optional()
      .default("curve")
      .describe(
        "Which reference of the element: 'curve' = the line itself (must be perpendicular to the dimension), 'start'/'end' = curve end points; for family instances (detail components) 'center' or a named reference plane"
      ),
  })
  .strict();

const dimensionSchema = z
  .object({
    startPoint: point3Schema
      .optional()
      .describe("Start of the measured span (mm). With endPoint it sets the dimension direction"),
    endPoint: point3Schema.optional().describe("End of the measured span (mm)"),
    linePoint: point3Schema
      .optional()
      .describe("A point the dimension line passes through (mm); defaults to startPoint"),
    references: z
      .array(referenceSchema)
      .min(2)
      .max(200)
      .optional()
      .describe(
        "Explicit references in chain order; one dimension gets one segment per gap (e.g. 3\" 3\" 2\" 2\" 3\" 3\"). Needs startPoint/endPoint or linePoint"
      ),
    elementIds: z
      .array(elementIdSchema)
      .max(200)
      .optional()
      .describe("At least 2 model elements (walls, doors, windows, grids) whose faces aligned with startPoint->endPoint are dimensioned"),
    dimensionType: z
      .string()
      .min(1)
      .max(256)
      .optional()
      .describe("Linear dimension type name: exact match (case-insensitive) first, then a unique partial match"),
    dimensionStyleId: z
      .number()
      .int()
      .optional()
      .describe("Dimension type element id; wins over dimensionType. Prefer this to avoid picking a similar type"),
    viewId: z.number().int().optional().describe("View to dimension in; default the active view"),
    snapToleranceMm: z
      .number()
      .positive()
      .max(1000)
      .optional()
      .describe("Model-space search radius for point snapping (default 2 mm on paper x view scale)"),
    text: dimensionTextSchema.optional(),
    segments: dimensionSegmentsTextSchema.optional(),
  })
  .superRefine((d, ctx) => {
    const issue = (message: string) => ctx.addIssue({ code: z.ZodIssueCode.custom, message });
    if ((d.startPoint === undefined) !== (d.endPoint === undefined)) issue("startPoint and endPoint go together");
    const elementCount = d.elementIds?.length ?? 0;
    if (elementCount === 1) issue("elementIds needs at least 2 elements");
    if (d.references && elementCount > 0) issue("Use either references or elementIds, not both");
    const hasSpan = d.startPoint !== undefined && d.endPoint !== undefined;
    if (d.references ? !hasSpan && !d.linePoint : !hasSpan)
      issue(d.references ? "references need startPoint/endPoint or linePoint" : "startPoint and endPoint are required");
  });

export function registerCreateDimensionsTool(server: McpServer) {
  server.tool(
    "create_dimensions",
    "Create linear dimensions (mm) in a view, drafting view or plan. Three ways to pick what is measured: (1) references: explicit element references in chain order - in drafting/detail views reference detail lines ('curve' for lines perpendicular to the dimension, 'start'/'end' for line end points) to build one multi-segment chain; (2) startPoint/endPoint alone: each point snaps to the nearest detail line, line end point or detail component within snapToleranceMm, and the call fails instead of creating a 0 or single-reference dimension; (3) elementIds: faces of walls/doors/windows along startPoint->endPoint. Set text overrides/prefix/suffix/above/below with 'text' (all segments) or 'segments' (per index). Pass dimensionStyleId (type id from get_view_annotations or a type listing) to avoid resolving a similarly named type such as '... - Blank'. Returns per dimension: id, segment count, values in mm, referenced element ids and text warnings.",
    {
      dimensions: z.array(dimensionSchema).min(1).max(200).describe("Dimensions to create"),
    },
    async (args) => sendDocumentationCommand("create_dimensions", args)
  );
}
