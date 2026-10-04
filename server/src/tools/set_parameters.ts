import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { elementFilterShape, hasSelection, levelListSchema } from "../utils/elementFilterSchemas.js";
import { bulkInputShape } from "../utils/bulkInput.js";
import { runBulkCommand } from "../utils/bulkCommand.js";

/** Placeholder format suffixes understood by the Revit side ({b:mm0}, {Reference Level:id}, ...). */
export const EXPRESSION_FORMAT = /^(?:(?:mm|cm|m)\d?|display|raw|id|upper|lower)$/i;

export interface ExpressionPlaceholder {
  name: string;
  format?: string;
}

/**
 * Parses an expression template such as "{Mark}-{b:mm0}x{h}" and returns its
 * placeholders; throws on unbalanced braces, empty names or unknown formats.
 * "{{" and "}}" are literal braces. Mirrors ParameterExpression.cs.
 */
export function parseExpression(expression: string): ExpressionPlaceholder[] {
  const placeholders: ExpressionPlaceholder[] = [];
  for (let i = 0; i < expression.length; i++) {
    const c = expression[i];
    if (c === "{") {
      if (expression[i + 1] === "{") {
        i++;
        continue;
      }
      const end = expression.indexOf("}", i + 1);
      if (end < 0) throw new Error(`Unclosed '{' at position ${i}`);
      const inner = expression.slice(i + 1, end);
      if (inner.includes("{")) throw new Error(`Nested '{' at position ${i}`);
      const colon = inner.lastIndexOf(":");
      const name = (colon > 0 ? inner.slice(0, colon) : inner).trim();
      const format = colon > 0 ? inner.slice(colon + 1).trim() : undefined;
      if (!name) throw new Error(`Empty placeholder at position ${i}`);
      if (format !== undefined && !EXPRESSION_FORMAT.test(format)) {
        throw new Error(`Unknown format '${format}' in {${inner}} (use mm, mm0-mm9, cm1, m2, display, raw, id, upper, lower)`);
      }
      placeholders.push(format === undefined ? { name } : { name, format: format.toLowerCase() });
      i = end;
      continue;
    }
    if (c === "}") {
      if (expression[i + 1] === "}") {
        i++;
        continue;
      }
      throw new Error(`Unmatched '}' at position ${i}`);
    }
  }
  return placeholders;
}

const expressionSchema = z
  .string()
  .min(1)
  .max(1024)
  .superRefine((value, ctx) => {
    try {
      parseExpression(value);
    } catch (error) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, message: error instanceof Error ? error.message : String(error) });
    }
  });

const parameterValueSchema = z.union([z.string().max(4096), z.number().finite(), z.boolean(), z.null()]);

const EXPRESSION_HELP =
  "Template with {Parameter} placeholders read from the element, then from its type, e.g. \"{Mark}-{b}x{h}\" -> \"B12-300x600\". " +
  "Length values are written in mm without units (format suffix {b:mm0}, {b:mm1}, {b:cm1}, {b:m2}; {x:display} = Revit's display string with units, {x:raw} = internal number). " +
  "Element-id values give the element name ({Reference Level} -> 'L5'; {Reference Level:id} -> the id). " +
  "Special names: {Type Name}, {Family Name}, {Family and Type}, {Category}, {Level} (the element's base/reference level), {Element Id}, {Type Id}. {x:upper}/{x:lower} change case; {{ and }} are literal braces";

export const setParameterItemSchema = z
  .object({
    elementId: elementIdSchema.describe("Element (or element type) id"),
    parameter: z.string().min(1).max(256).describe("Parameter name as shown in Properties"),
    value: parameterValueSchema.optional().describe("Value: text, number (lengths in mm), true/false for Yes/No, element id or level/material name for element-id parameters"),
    expression: expressionSchema.optional().describe("Template instead of value (see the tool description)"),
    onType: z.boolean().optional().describe("Write the element's type parameter instead of the instance"),
  })
  .strict()
  .refine((item) => (item.value !== undefined) !== (item.expression !== undefined), {
    message: "Give exactly one of value or expression",
  });

export type SetParameterItem = z.infer<typeof setParameterItemSchema>;

export const setParametersShape = {
  ...elementFilterShape,
  levels: levelListSchema.optional(),
  parameter: z.string().min(1).max(256).optional().describe('Filter mode: parameter to write, e.g. "Comments", "Mark" or a project parameter like "Tag Text"'),
  value: parameterValueSchema.optional().describe("Filter mode: the same value for every selected element (lengths in mm)"),
  expression: expressionSchema.optional().describe(`Filter mode: per-element value. ${EXPRESSION_HELP}`),
  onType: z
    .boolean()
    .optional()
    .describe("Write a type parameter: each distinct type of the selected elements is set once (expressions are evaluated on the type's first selected instance; differing results are reported as conflicts)"),
  skipIfUnresolved: z
    .boolean()
    .optional()
    .default(true)
    .describe("Default true: skip elements whose expression has a missing or empty placeholder (reported per placeholder). false writes them with the placeholder left empty"),
  dryRun: z.boolean().optional().default(false).describe("Only compute and report the values (samples, unresolved placeholders) without writing"),
  maxElements: z
    .number()
    .int()
    .min(1)
    .max(100000)
    .optional()
    .default(20000)
    .describe("Filter mode: refuse to run when more elements match"),
  items: z
    .array(setParameterItemSchema)
    .max(5000)
    .optional()
    .describe("Explicit mode instead of filters: [{elementId, parameter, value | expression, onType?}]"),
  ...bulkInputShape,
};

const setParametersSchema = z.object(setParametersShape);
export type SetParametersArgs = z.infer<typeof setParametersSchema>;

/** Returns an error message for an inconsistent request, or null. */
export function validateSetParametersArgs(args: Partial<SetParametersArgs>): string | null {
  const explicit = (args.items?.length ?? 0) > 0 || args.dataFile !== undefined;
  if (explicit) {
    if (hasSelection(args) || args.parameter !== undefined || args.value !== undefined || args.expression !== undefined) {
      return "Use either items/dataFile (explicit mode) or categories/elementIds + parameter + value/expression (filter mode), not both.";
    }
    return null;
  }
  if (!hasSelection(args)) return "Give categories and/or elementIds (filter mode) or items/dataFile (explicit mode).";
  if (!args.parameter) return "Filter mode needs 'parameter'.";
  if ((args.value !== undefined) === (args.expression !== undefined)) return "Give exactly one of value or expression.";
  return null;
}

export function registerSetParametersTool(server: McpServer) {
  server.tool(
    "set_parameters",
    "Bulk-write a parameter on many elements. Filter mode: select by categories/elementIds narrowed by levels, commentsEquals/commentsStartsWith/markStartsWith/typeNameEquals (ANDed, like delete_elements) and write `parameter` = `value`, or `expression` evaluated per element. " +
      `${EXPRESSION_HELP}. ` +
      "Example: categories:[\"StructuralFraming\"], parameter:\"Tag Text\", expression:\"{Mark}-{b}x{h}\" composes beam tag text for a tag type that displays 'Tag Text' (create it with create_project_parameter). " +
      "onType:true writes type parameters, each distinct type once. Explicit mode: items [{elementId, parameter, value|expression, onType?}] and/or a dataFile (JSON/JSONL/CSV), sent in chunks of 500, one undo step per chunk. " +
      "Lengths are mm. Reports set / unchanged / skipped counts, unresolved placeholders, conflicts, failures and sample values; dryRun previews without writing.",
    setParametersShape,
    async (args) => {
      const problem = validateSetParametersArgs(args);
      if (problem) {
        return { content: [{ type: "text" as const, text: `set_parameters: ${problem}` }], isError: true };
      }
      if ((args.items?.length ?? 0) > 0 || args.dataFile !== undefined) {
        const base: Record<string, unknown> = { skipIfUnresolved: args.skipIfUnresolved, dryRun: args.dryRun };
        if (args.onType !== undefined) base.onType = args.onType;
        return runBulkCommand<SetParameterItem>("set_parameters", args, "items", {
          itemSchema: setParameterItemSchema,
          stringFields: ["parameter", "expression", "value"],
          base,
          chunkSize: 500,
        });
      }
      const { items: _items, dataFile: _dataFile, dataFormat: _dataFormat, summary: _summary, ...filterArgs } = args;
      return sendDocumentationCommand("set_parameters", filterArgs, 600000);
    }
  );
}
