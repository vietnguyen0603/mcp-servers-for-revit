import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

export const dimensionElementsSchema = {
  viewId: z.number().int().positive().optional().describe("Plan view to dimension (default the active view)"),
  categories: z
    .array(z.string().min(1).max(128))
    .min(1)
    .max(10)
    .optional()
    .describe("Categories to dimension (default ['StructuralColumns','Walls']); also 'StructuralFoundation' for pile caps/footings"),
  elementIds: z.array(z.number().int().positive()).max(5000).optional().describe("Only these elements"),
  toGrids: z.boolean().optional().describe("Include the nearest parallel grid in each chain, e.g. 550 | 550 from the grid (default true)"),
  maxGridDistanceMm: z.number().positive().max(50000).optional().describe("Only grids within this distance of the element centre (default 3000)"),
  wallDimensions: z
    .array(z.enum(["thickness", "length"]))
    .min(1)
    .optional()
    .describe("Wall dimensions: thickness (across the wall, with the nearest parallel grid) and/or length of each straight wall (default both)"),
  minWallLengthMm: z.number().positive().max(100000).optional().describe("Skip length dimensions of shorter walls (default 1500)"),
  offsetPaperMm: z.number().positive().max(100).optional().describe("Distance of the dimension line outside the element, printed mm (default 6)"),
  side: z.enum(["bottomLeft", "topRight"]).optional().describe("Column/footing dimension side (default bottomLeft, away from tags placed top-right)"),
  dimensionType: z.string().min(1).max(256).optional().describe("Linear dimension type name (default the project default)"),
  replaceExisting: z.boolean().optional().describe("Delete this view's dimensions that reference the selected elements first (re-runs)"),
};

export function registerDimensionElementsTool(server: McpServer) {
  server.tool(
    "dimension_elements",
    "Dimension a column / shear wall layout plan like structural drawings: for each column or footing, per local axis (rotated columns too) a chain face-to-face through the nearest parallel grid (e.g. 550 | 550, or 750 | 1050 when off-centre); for each wall, its thickness across the wall tied to the nearest parallel grid and the length of each straight wall. " +
      "Dimension lines are placed offsetPaperMm outside the element. Snap element positions to round values first - dimensions show any extraction error (e.g. 5 | 1095 instead of 550 | 550). Use replaceExisting for re-runs. Plan views only; one undo step.",
    dimensionElementsSchema,
    async (args) => sendDocumentationCommand("dimension_elements", args, 330000)
  );
}
