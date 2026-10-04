import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

/**
 * manage_graphics_standards: create/update line patterns, line styles, fill
 * patterns and object styles (and list them) so an agent can set up a
 * project's graphic standards in one undoable call.
 */

export const GRAPHICS_LIST_SECTIONS = ["linePatterns", "lineStyles", "fillPatterns", "objectStyles"] as const;

const nameSchema = z.string().trim().min(1).max(256);

const rgbSchema = z
  .tuple([z.number().int().min(0).max(255), z.number().int().min(0).max(255), z.number().int().min(0).max(255)])
  .describe("Colour [r, g, b], 0-255, e.g. [128,128,128] for gray");

const penSchema = z.number().int().min(1).max(16).describe("Pen number 1-16 from the project's line weight table");

const ifExistsSchema = z
  .enum(["update", "skip", "error"])
  .optional()
  .default("update")
  .describe("What to do when an item with this name already exists: update it (default), skip it, or report an error");

const patternRefSchema = nameSchema.describe("Line pattern name (existing or created in this call), or 'Solid'");

const lengthMmSchema = z.number().finite().positive().max(10000);

const linePatternSegmentSchema = z
  .object({
    kind: z.enum(["dash", "space", "dot"]).describe("Segment kind"),
    lengthMm: z
      .number()
      .finite()
      .min(0)
      .max(1000)
      .optional()
      .describe("Printed length on paper in mm (required for dash and space; ignored for dot)"),
  })
  .strict();

const linePatternSchema = z
  .object({
    name: nameSchema.describe("Line pattern name, e.g. 'S-HIDDEN'"),
    segments: z
      .array(linePatternSegmentSchema)
      .min(2)
      .max(64)
      .describe(
        "Repeating sequence: starts with a dash or dot, then alternates visible segment / space, even count. " +
          "Example dashed: [{kind:'dash',lengthMm:3},{kind:'space',lengthMm:1.5}]"
      ),
    ifExists: ifExistsSchema,
  })
  .strict()
  .superRefine((pattern, ctx) => {
    if (pattern.segments.length % 2 !== 0) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["segments"], message: "A line pattern needs an even number of segments (visible, space, visible, space, ...)." });
    }
    pattern.segments.forEach((segment, index) => {
      const shouldBeSpace = index % 2 === 1;
      if (shouldBeSpace !== (segment.kind === "space")) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ["segments", index, "kind"],
          message: shouldBeSpace
            ? "Segments must alternate: every odd position (1, 3, ...) must be a space."
            : "Segments must alternate: every even position (0, 2, ...) must be a dash or dot.",
        });
      }
      if (segment.kind !== "dot" && !(segment.lengthMm !== undefined && segment.lengthMm > 0)) {
        ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["segments", index, "lengthMm"], message: `A ${segment.kind} needs lengthMm > 0.` });
      }
    });
  });

const lineStyleSchema = z
  .object({
    name: nameSchema.describe("Line style name (a subcategory of Lines), e.g. 'S-HIDDEN' or 'PEN3'"),
    weight: penSchema.optional().describe("Projection line weight, pen 1-16"),
    color: rgbSchema.optional(),
    pattern: patternRefSchema.optional(),
    ifExists: ifExistsSchema,
  })
  .strict();

const fillGridSchema = z
  .object({
    angleDeg: z.number().finite().min(-360).max(360).optional().default(0).describe("Line direction in degrees"),
    originMm: z
      .object({ x: z.number().finite(), y: z.number().finite() })
      .strict()
      .optional()
      .describe("Grid origin in mm (default 0,0)"),
    offsetMm: lengthMmSchema.describe("Distance between parallel lines in mm (> 0)"),
    shiftMm: z.number().finite().optional().default(0).describe("Shift of the dash pattern along each next line, mm"),
    segments: z
      .array(lengthMmSchema)
      .max(32)
      .optional()
      .default([])
      .describe("Dash/space lengths in mm alternating dash, space, ... (even count); empty = continuous line"),
  })
  .strict()
  .refine((grid) => grid.segments.length % 2 === 0, {
    message: "Grid segments must alternate dash and space lengths (even count).",
    path: ["segments"],
  });

const fillPatternSchema = z
  .object({
    name: nameSchema.describe("Fill pattern name, e.g. 'Concrete', 'Earth', 'PC-1150'"),
    target: z
      .enum(["drafting", "model"])
      .optional()
      .default("drafting")
      .describe("drafting: sizes are printed paper mm (scale with the view); model: sizes are real mm on the element"),
    hostOrientation: z
      .enum(["toView", "toHost", "asText"])
      .optional()
      .describe("Pattern orientation (default toView for drafting, toHost for model)"),
    solid: z.literal(true).optional().describe("Solid fill (no hatch lines)"),
    simple: z
      .object({
        angleDeg: z.number().finite().min(-360).max(360).optional().default(0),
        spacingMm: lengthMmSchema.describe("Line spacing in mm"),
        crosshatch: z.boolean().optional().default(false).describe("Add a perpendicular set of lines"),
        crossSpacingMm: lengthMmSchema.optional().describe("Spacing of the second set (default spacingMm)"),
      })
      .strict()
      .optional()
      .describe("Parallel hatch (or crosshatch) - quickest way to make e.g. a 45 deg hatch"),
    grids: z
      .array(fillGridSchema)
      .min(1)
      .max(32)
      .optional()
      .describe("Custom pattern grids (like a .pat file), for concrete/earth/dashed hatches"),
    ifExists: ifExistsSchema,
  })
  .strict()
  .superRefine((pattern, ctx) => {
    const kinds = [pattern.solid ? 1 : 0, pattern.simple ? 1 : 0, pattern.grids ? 1 : 0].reduce((a, b) => a + b, 0);
    if (kinds !== 1) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, message: "Give exactly one of solid, simple or grids." });
    }
  });

const objectStyleSchema = z
  .object({
    category: nameSchema.describe(
      "Category ('Structural Framing' or 'OST_StructuralFraming'), or 'Category/Subcategory' e.g. 'Structural Framing/Hidden Lines'. Names ignore case, extra spaces and surrounding <> (Revit's '<Hidden Lines>' matches 'Hidden Lines'); unknown names list close matches"
    ),
    projectionWeight: penSchema.optional().describe("Projection line weight, pen 1-16"),
    cutWeight: penSchema.optional().describe("Cut line weight, pen 1-16 (cuttable categories only)"),
    color: rgbSchema.optional().describe("Line colour [r, g, b]"),
    pattern: patternRefSchema.optional(),
    material: nameSchema.optional().describe("Default material name for the category"),
  })
  .strict()
  .refine(
    (style) =>
      style.projectionWeight !== undefined ||
      style.cutWeight !== undefined ||
      style.color !== undefined ||
      style.pattern !== undefined ||
      style.material !== undefined,
    { message: "Nothing to change: give projectionWeight, cutWeight, color, pattern or material." }
  );

const penWidthSchema = z
  .object({ pen: penSchema, widthMm: z.number().finite().positive().max(10) })
  .strict();

const lineWeightsSchema = z
  .object({
    model: z
      .array(penWidthSchema.extend({ scale: z.string().trim().min(1).max(32).optional().describe("e.g. '1:100'") }).strict())
      .max(256)
      .optional(),
    perspective: z.array(penWidthSchema).max(16).optional(),
    annotation: z.array(penWidthSchema).max(16).optional(),
  })
  .strict()
  .describe(
    "Pen widths of the Line Weights tables. NOT supported by the Revit API: the call returns a note and changes nothing - set them in Manage > Additional Settings > Line Weights or Transfer Project Standards from a template."
  );

export function registerManageGraphicsStandardsTool(server: McpServer) {
  server.tool(
    "manage_graphics_standards",
    "Set up or inspect the project's graphic standards in one undoable call: line patterns (e.g. 'S-HIDDEN' dashed), line styles (Lines subcategories with pen weight, colour, pattern), fill patterns (solid, simple/cross hatch, or custom grids for concrete/earth/thickness-coded hatches, drafting or model) and object styles (category/subcategory projection & cut weights, colour, pattern, material). " +
      "Sections are applied in order linePatterns, fillPatterns, lineStyles, objectStyles, so a style can use a pattern created in the same call. Lengths are mm: line pattern and drafting fill sizes are printed paper mm, model fill sizes are real mm. " +
      "Each item reports created/updated/skipped/failed plus warnings; failures don't undo the other items. lineWeights (pen width tables) cannot be set through the Revit API - a note is returned. " +
      "list=true returns existing line patterns, line styles, fill patterns and model-category object styles (read-only when nothing else is given); filter with listOptions.",
    {
      linePatterns: z.array(linePatternSchema).min(1).max(100).optional().describe("Line patterns to create/update"),
      lineStyles: z.array(lineStyleSchema).min(1).max(200).optional().describe("Line styles to create/update"),
      fillPatterns: z.array(fillPatternSchema).min(1).max(100).optional().describe("Fill patterns to create/update"),
      objectStyles: z.array(objectStyleSchema).min(1).max(200).optional().describe("Object styles to modify (existing categories/subcategories only)"),
      lineWeights: lineWeightsSchema.optional(),
      list: z.boolean().optional().describe("Return the existing standards (after any changes in this call)"),
      listOptions: z
        .object({
          sections: z.array(z.enum(GRAPHICS_LIST_SECTIONS)).min(1).max(GRAPHICS_LIST_SECTIONS.length).optional().describe("Sections to list (default all)"),
          nameContains: z.string().trim().min(1).max(256).optional().describe("Case-insensitive name filter"),
          includeSubcategories: z.boolean().optional().describe("Object styles: include subcategories (default false)"),
          includeAnnotationCategories: z.boolean().optional().describe("Object styles: include annotation categories too (default model only)"),
        })
        .strict()
        .optional(),
    },
    async (args) => {
      const hasSection =
        !!args.linePatterns?.length ||
        !!args.lineStyles?.length ||
        !!args.fillPatterns?.length ||
        !!args.objectStyles?.length ||
        args.lineWeights !== undefined;
      if (!hasSection && !args.list) {
        return {
          content: [
            {
              type: "text" as const,
              text: "manage_graphics_standards needs at least one of linePatterns, lineStyles, fillPatterns, objectStyles, lineWeights, or list=true.",
            },
          ],
          isError: true,
        };
      }
      return sendDocumentationCommand("manage_graphics_standards", args);
    }
  );
}
