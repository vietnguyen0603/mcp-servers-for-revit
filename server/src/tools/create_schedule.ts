import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

const fieldName = z.string().min(1).max(256);

const FILTER_OPERATORS = [
  "Equal",
  "NotEqual",
  "GreaterThan",
  "GreaterThanOrEqual",
  "LessThan",
  "LessThanOrEqual",
  "Contains",
  "NotContains",
  "BeginsWith",
  "NotBeginsWith",
  "EndsWith",
  "NotEndsWith",
  "HasValue",
  "HasNoValue",
] as const;

export function registerCreateScheduleTool(server: McpServer) {
  server.tool(
    "create_schedule",
    "Create a Revit schedule for a category with fields, filters and sorting/grouping. Fields are matched by their schedulable name (as shown in Revit's Schedule Properties), case-insensitively; unknown fields are reported as warnings, and if none match the available field names are listed. Fields used only for filtering or sorting are added hidden. Numeric filter values on length fields are millimetres.",
    {
      category: z
        .string()
        .min(1)
        .max(256)
        .describe(
          "Category as a BuiltInCategory name (OST_StructuralFraming or StructuralFraming), a category display name (Structural Framing), or MultiCategory"
        ),
      name: z.string().min(1).max(256).optional().describe("Schedule name; must be unique"),
      fields: z
        .array(
          z.union([
            fieldName,
            z.object({
              name: fieldName,
              heading: z.string().max(256).optional().describe("Column heading override"),
              hidden: z.boolean().optional(),
            }),
          ])
        )
        .min(1)
        .max(64)
        .describe("Fields in column order"),
      filters: z
        .array(
          z.object({
            field: fieldName,
            operator: z.enum(FILTER_OPERATORS).default("Equal"),
            value: z.union([z.string().max(1024), z.number().finite()]).optional(),
          })
        )
        .max(8)
        .optional()
        .describe("Up to 8 filters, combined with AND"),
      sortBy: z
        .array(
          z.object({
            field: fieldName,
            order: z.enum(["Ascending", "Descending"]).default("Ascending"),
            showHeader: z.boolean().optional().describe("Group header row"),
            showFooter: z.boolean().optional().describe("Group footer row (count/totals)"),
            blankLine: z.boolean().optional().describe("Blank line between groups"),
          })
        )
        .max(8)
        .optional()
        .describe("Sorting/grouping levels in priority order"),
      itemized: z.boolean().optional().default(true).describe("Itemize every instance (false groups identical rows)"),
    },
    async (args) => sendDocumentationCommand("create_schedule", args)
  );
}
