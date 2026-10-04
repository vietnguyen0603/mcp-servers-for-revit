import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

const nameFilter = z
  .array(z.string().min(1).max(256))
  .min(1)
  .max(20);

export const dimensionElementsSchema = {
  action: z
    .enum(["dimension", "retype"])
    .optional()
    .describe(
      "dimension (default): draft element dimensions. retype: change the DimensionType of existing linear dimensions in viewIds (or viewId / the active view) to dimensionType, e.g. grid dimensions made before the office style existed"
    ),
  viewId: z.number().int().positive().optional().describe("Plan view to dimension (default the active view)"),
  viewIds: z
    .array(z.number().int().positive())
    .min(1)
    .max(500)
    .optional()
    .describe("retype only: views whose linear dimensions are retyped (default viewId or the active view)"),
  onlyFromTypes: nameFilter
    .optional()
    .describe("retype only: change only dimensions currently of these type names (case-insensitive); default every linear dimension"),
  categories: z
    .array(z.string().min(1).max(128))
    .min(1)
    .max(10)
    .optional()
    .describe("Categories to dimension (default ['StructuralColumns','Walls']); also 'StructuralFoundation' for pile caps/footings"),
  elementIds: z.array(z.number().int().positive()).max(5000).optional().describe("Only these elements"),
  typeNameContains: nameFilter
    .optional()
    .describe("Only elements whose type name contains any of these texts (case-insensitive), e.g. ['PC'] for pile caps"),
  familyNameContains: nameFilter
    .optional()
    .describe("Only elements whose family name contains any of these texts (case-insensitive), e.g. ['Pile Cap'] to skip piles in StructuralFoundation"),
  toGrids: z.boolean().optional().describe("Include the nearest parallel grid in each chain, e.g. 550 | 550 from the grid (default true)"),
  maxGridDistanceMm: z.number().positive().max(50000).optional().describe("Only grids within this distance of the element centre (default 3000)"),
  wallDimensions: z
    .array(z.enum(["thickness", "length"]))
    .min(1)
    .optional()
    .describe("Wall dimensions: thickness (across the wall, with the nearest parallel grid) and/or length of each straight wall run (default both)"),
  wallRuns: z
    .boolean()
    .optional()
    .describe(
      "Merge collinear wall pieces of the same thickness that touch end to end into one run, dimensioned once (default true). Run ends joined to a perpendicular wall measure to that wall's outer face at a corner (L) or its near face at a T"
    ),
  minWallLengthMm: z.number().positive().max(100000).optional().describe("Skip length dimensions of shorter walls/runs (default 1500)"),
  offsetPaperMm: z.number().positive().max(100).optional().describe("Distance of the dimension line outside the element, printed mm (default 6)"),
  side: z.enum(["bottomLeft", "topRight"]).optional().describe("Column/footing dimension side (default bottomLeft, away from tags placed top-right)"),
  dimensionType: z
    .string()
    .min(1)
    .max(256)
    .optional()
    .describe("Linear dimension type name (default the project default); required for action retype"),
  replaceExisting: z
    .boolean()
    .optional()
    .describe(
      "First delete this view's dimensions referencing any element of the selected categories that passes the filters (elementIds/typeNameContains/familyNameContains), so re-runs leave no stale dimensions"
    ),
};

type DimensionElementsArgs = z.infer<z.ZodObject<typeof dimensionElementsSchema>>;

/** Cross-field checks the flat schema cannot express. Returns an error message or null. */
export function validateDimensionElementsArgs(args: DimensionElementsArgs): string | null {
  const retype = args.action === "retype";
  if (retype) {
    if (!args.dimensionType) return "action 'retype' needs dimensionType (the target linear dimension type name).";
    const dimensionOnly = [
      "categories",
      "elementIds",
      "typeNameContains",
      "familyNameContains",
      "toGrids",
      "maxGridDistanceMm",
      "wallDimensions",
      "wallRuns",
      "minWallLengthMm",
      "offsetPaperMm",
      "side",
      "replaceExisting",
    ] as const;
    const used = dimensionOnly.filter((k) => args[k] !== undefined);
    if (used.length > 0) return `action 'retype' does not use: ${used.join(", ")}.`;
  } else {
    if (args.viewIds !== undefined || args.onlyFromTypes !== undefined)
      return "viewIds and onlyFromTypes are only for action 'retype'.";
  }
  return null;
}

export function registerDimensionElementsTool(server: McpServer) {
  server.tool(
    "dimension_elements",
    "Dimension a column / shear wall layout plan like structural drawings: for each column or footing, per local axis (rotated columns too) a chain face-to-face through the nearest parallel grid (e.g. 550 | 550, or 750 | 1050 when off-centre); for walls, collinear pieces of the same thickness joined end to end are merged into runs (wallRuns, default true) and each run gets its thickness once (tied to the nearest parallel grid) and its length once, read to the outer face of perpendicular walls at corners. " +
      "Narrow the selection with elementIds, typeNameContains and familyNameContains (e.g. pile caps without piles). Dimension lines are placed offsetPaperMm outside the element. Snap element positions to round values first - dimensions show any extraction error (e.g. 5 | 1095 instead of 550 | 550). " +
      "replaceExisting deletes this view's dimensions on any selected-category element passing the filters before drafting (reported as dimensionsReplaced). " +
      "action 'retype' instead changes the DimensionType of existing linear dimensions in viewIds to dimensionType (optionally onlyFromTypes). Plan views only for drafting; one undo step.",
    dimensionElementsSchema,
    async (args) => {
      const error = validateDimensionElementsArgs(args);
      if (error) {
        return { content: [{ type: "text" as const, text: `dimension_elements: ${error}` }], isError: true };
      }
      return sendDocumentationCommand("dimension_elements", args, 330000);
    }
  );
}
