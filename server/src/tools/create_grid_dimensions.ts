import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerCreateGridDimensionsTool(server: McpServer) {
  server.tool(
    "create_grid_dimensions",
    "Dimension grids in a plan, section or elevation view. Linear grids are grouped by direction (parallel within 0.5°), ordered across that direction, and dimensioned with a chain string (every grid) and an overall string (first to last grid) near the chosen grid end(s). Offsets are model millimetres measured inward from the grid end towards the model; the overall string sits overallOffsetMm closer to the grid end than the chain. Arc grids are skipped. By default a dimension is not created if one in the view already references exactly the same grids.",
    {
      viewId: elementIdSchema.optional().describe("Plan, section or elevation view (default active view)"),
      gridIds: z
        .array(elementIdSchema)
        .max(500)
        .optional()
        .describe("Grids to dimension (default: all grids visible in the view)"),
      chain: z.boolean().optional().default(true).describe("Create a chain dimension through every grid in a group"),
      overall: z
        .boolean()
        .optional()
        .default(true)
        .describe("Create an overall dimension from the first to the last grid (only for groups of 3+ grids when chain is on)"),
      offsetMm: z
        .number()
        .finite()
        .optional()
        .default(1500)
        .describe("Chain dimension line distance inward from the grid end, in model mm"),
      overallOffsetMm: z
        .number()
        .finite()
        .optional()
        .default(800)
        .describe("Extra distance of the overall line beyond the chain, towards the grid end, in model mm"),
      sides: z
        .array(z.enum(["Start", "End"]))
        .min(1)
        .max(2)
        .optional()
        .default(["Start"])
        .describe("Grid end(s) to dimension at; Start is the end where each group's grids begin"),
      dimensionTypeId: elementIdSchema.optional().describe("Linear dimension type to use"),
      skipExisting: z
        .boolean()
        .optional()
        .default(true)
        .describe("Skip a dimension if the view already has one referencing exactly the same grids"),
    },
    async (args) => sendDocumentationCommand("create_grid_dimensions", args)
  );
}
