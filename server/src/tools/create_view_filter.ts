import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { graphicOverridesSchema } from "../utils/graphicOverrideSchema.js";

const VALUELESS_OPERATORS = ["HasValue", "HasNoValue"];

const ruleSchema = z
  .object({
    parameter: z
      .string()
      .min(1)
      .max(256)
      .describe("Parameter display name (e.g. 'Mark', 'Type Mark') or BuiltInParameter name (e.g. ALL_MODEL_MARK)"),
    operator: z
      .enum([
        "Equals",
        "NotEquals",
        "Greater",
        "GreaterOrEqual",
        "Less",
        "LessOrEqual",
        "Contains",
        "NotContains",
        "BeginsWith",
        "NotBeginsWith",
        "EndsWith",
        "NotEndsWith",
        "HasValue",
        "HasNoValue",
      ])
      .default("Equals")
      .describe("HasValue/HasNoValue require Revit 2022+"),
    value: z
      .union([z.string().max(1024), z.number().finite()])
      .optional()
      .describe("Comparison value; numbers on length parameters are millimetres"),
  })
  .superRefine((rule, ctx) => {
    if (!VALUELESS_OPERATORS.includes(rule.operator) && rule.value === undefined) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, message: `${rule.operator} requires a value` });
    }
  });

export function registerCreateViewFilterTool(server: McpServer) {
  server.tool(
    "create_view_filter",
    "Create a rule-based view filter (parameter filter) for one or more categories and add it to views with optional graphic overrides and visibility. Rule parameters must be filterable for every listed category. Without rules the filter matches all elements of the categories. Set reuseExisting to add an existing filter with the same name to views instead of failing. Views controlled by a view template report an error suggesting the template.",
    {
      name: z.string().min(1).max(256).describe("Filter name; must be unique unless reuseExisting is true"),
      categories: z
        .array(z.string().min(1).max(256))
        .min(1)
        .max(64)
        .describe("Categories, e.g. OST_StructuralFraming or 'Structural Framing'"),
      rules: z.array(ruleSchema).max(32).optional().describe("Filter rules"),
      logic: z.enum(["And", "Or"]).optional().default("And").describe("How rules combine"),
      viewIds: z.array(elementIdSchema).max(500).optional().describe("Views or view templates to add the filter to"),
      overrides: graphicOverridesSchema.optional().describe("Graphic overrides applied to the filter in each view"),
      visible: z.boolean().optional().describe("Filter visibility in each view"),
      reuseExisting: z.boolean().optional().default(false),
    },
    async (args) => sendDocumentationCommand("create_view_filter", args)
  );
}
