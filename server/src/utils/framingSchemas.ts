import { z } from "zod";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { elementIdSchema } from "./documentationSchemas.js";
import { formatBulkResult, loadItems, sendInChunks, type BulkInputArgs } from "./bulkInput.js";

/** Shared schemas and the bulk pipeline for create_structural_columns and create_beams. */

export const MAX_INLINE_ITEMS = 2000;
export const MAX_TOTAL_ITEMS = 20000;

export const levelRefSchema = z
  .union([z.string().min(1).max(256), z.number().finite()])
  .describe("Level name (case-insensitive), level element id, or elevation in mm (matched within 1 mm)");

export const xySchema = z.object({
  x: z.number().finite().describe("X in millimetres"),
  y: z.number().finite().describe("Y in millimetres"),
});

export const typeRefShape = {
  typeId: elementIdSchema.optional().describe("Family type (symbol) id"),
  familyName: z.string().min(1).max(256).optional().describe("Loaded family name, e.g. 'M_WWF-Welded Wide Flange'"),
  typeName: z
    .string()
    .min(1)
    .max(256)
    .optional()
    .describe("Type of familyName (required when the family has more than one type); exact, case-insensitive"),
};

export const markSchema = z.union([z.string().max(256), z.number().finite()]);

export function hasTypeRef(item: { typeId?: number; familyName?: string }) {
  return item.typeId !== undefined || item.familyName !== undefined;
}

export const typeRefMessage = { message: "Give typeId or familyName (+ typeName)" };

function errorResult(text: string): CallToolResult {
  return { content: [{ type: "text", text }], isError: true };
}

/**
 * Loads inline + file items, validates every item against `itemSchema`
 * (file items never passed the MCP boundary) and sends them in chunks.
 */
export async function runFramingBulk<T>(
  command: string,
  itemsKey: string,
  itemSchema: z.ZodType<T>,
  args: BulkInputArgs<unknown>
): Promise<CallToolResult> {
  let raw: unknown[];
  try {
    raw = await loadItems(args, itemsKey);
  } catch (error) {
    return errorResult(`${command} failed: ${error instanceof Error ? error.message : String(error)}`);
  }
  if (raw.length === 0) return errorResult(`${command} failed: give ${itemsKey} or dataFile with at least one item`);
  if (raw.length > MAX_TOTAL_ITEMS) {
    return errorResult(`${command} failed: ${raw.length} items exceed the limit of ${MAX_TOTAL_ITEMS} per call`);
  }

  const items: T[] = [];
  const problems: string[] = [];
  raw.forEach((item, index) => {
    const parsed = itemSchema.safeParse(item);
    if (parsed.success) {
      items.push(parsed.data);
    } else if (problems.length < 20) {
      const issues = parsed.error.issues.map((i) => `${i.path.join(".") || "(item)"}: ${i.message}`).join("; ");
      problems.push(`${itemsKey}[${index}]: ${issues}`);
    } else {
      problems.push("");
    }
  });
  if (problems.length > 0) {
    const shown = problems.filter(Boolean);
    const more = problems.length - shown.length;
    return errorResult(
      `${command} failed: ${problems.length} invalid item(s), nothing was sent (indexes count inline items first, then file items):\n` +
        shown.join("\n") +
        (more > 0 ? `\n... and ${more} more` : "")
    );
  }

  return formatBulkResult(command, await sendInChunks(command, {}, itemsKey, items), { summary: args.summary === true });
}
