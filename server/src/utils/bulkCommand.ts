import { z } from "zod";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { formatBulkResult, loadItems, sendInChunks, type DataFormat } from "./bulkInput.js";

/**
 * Glue for bulk modelling tools built on bulkInput.ts: load inline + file
 * items, decode JSON cells from CSV, validate every item with the tool's item
 * schema, send in chunks and render the merged result.
 */

/** CSV cells hold strings; decode the given fields when they contain JSON (e.g. a boundary "[{x:..}]"). */
export function decodeJsonFields(item: unknown, fields: readonly string[]): unknown {
  if (typeof item !== "object" || item === null || Array.isArray(item)) return item;
  const copy: Record<string, unknown> = { ...(item as Record<string, unknown>) };
  for (const field of fields) {
    const value = copy[field];
    if (typeof value !== "string") continue;
    const text = value.trim();
    if (!text.startsWith("[") && !text.startsWith("{")) continue;
    try {
      copy[field] = JSON.parse(text);
    } catch {
      // leave as is; validation reports it
    }
  }
  return copy;
}

/** CSV turns numeric cells into numbers; turn the given name-like fields back into strings (e.g. a type named 300). */
export function stringifyFields(item: unknown, fields: readonly string[]): unknown {
  if (typeof item !== "object" || item === null || Array.isArray(item)) return item;
  const copy: Record<string, unknown> = { ...(item as Record<string, unknown>) };
  for (const field of fields) {
    if (typeof copy[field] === "number" || typeof copy[field] === "boolean") copy[field] = String(copy[field]);
  }
  return copy;
}

/** Validates every item; throws listing the first invalid ones (indexes into the full list). */
export function validateItems<T>(items: unknown[], schema: z.ZodType<T>, itemsKey: string): T[] {
  const errors: string[] = [];
  const parsed = items.map((item, index) => {
    const result = schema.safeParse(item);
    if (result.success) return result.data;
    errors.push(
      `${itemsKey}[${index}]: ${result.error.issues.map((i) => `${i.path.join(".") || "(item)"} ${i.message}`).join("; ")}`
    );
    return undefined as unknown as T;
  });
  if (errors.length > 0) {
    const more = errors.length > 10 ? `\n... and ${errors.length - 10} more invalid items` : "";
    throw new Error(`${errors.length} invalid item(s):\n${errors.slice(0, 10).join("\n")}${more}`);
  }
  return parsed;
}

export interface BulkCommandOptions<T> {
  itemSchema: z.ZodType<T>;
  jsonFields?: readonly string[];
  /** Fields that must stay strings when a CSV cell looks numeric. */
  stringFields?: readonly string[];
  base?: object;
  chunkSize?: number;
}

export async function runBulkCommand<T>(
  command: string,
  args: { dataFile?: string; dataFormat?: DataFormat; summary?: boolean } & Record<string, unknown>,
  itemsKey: string,
  options: BulkCommandOptions<T>
): Promise<CallToolResult> {
  try {
    const loaded = await loadItems<unknown>(
      { items: args[itemsKey] as unknown[] | undefined, dataFile: args.dataFile, dataFormat: args.dataFormat },
      itemsKey
    );
    if (loaded.length === 0) throw new Error(`Give ${itemsKey} or a dataFile with at least one item`);
    let decoded = options.jsonFields ? loaded.map((item) => decodeJsonFields(item, options.jsonFields!)) : loaded;
    if (options.stringFields) decoded = decoded.map((item) => stringifyFields(item, options.stringFields!));
    const items = validateItems(decoded, options.itemSchema, itemsKey);
    const outcome = await sendInChunks(command, options.base ?? {}, itemsKey, items, options.chunkSize ?? 300);
    return formatBulkResult(command, outcome, { summary: args.summary === true });
  } catch (error) {
    return {
      content: [{ type: "text", text: `${command} failed: ${error instanceof Error ? error.message : String(error)}` }],
      isError: true,
    };
  }
}
