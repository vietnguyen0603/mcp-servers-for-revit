import { promises as fs } from "fs";
import path from "path";
import { z } from "zod";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { withRevitConnection } from "./ConnectionManager.js";
import { applyBatchOutcome } from "./batchOutcome.js";

/**
 * Bulk input for batch creation tools: items may be given inline, read from a
 * local data file (JSON, JSON Lines or CSV), or both, and are sent to Revit in
 * chunks so thousands of items fit in one tool call.
 */

export type DataFormat = "json" | "csv" | "jsonl";

export const bulkInputShape = {
  dataFile: z
    .string()
    .min(1)
    .max(1024)
    .optional()
    .describe(
      "Absolute path of a local data file with more items (use for hundreds/thousands of items instead of pasting them): a JSON array (or an object holding the items array), JSON Lines, or CSV with a header row - dotted headers such as start.x become nested fields, numeric cells become numbers. Inline items come first, then file items"
    ),
  dataFormat: z
    .enum(["json", "csv", "jsonl"])
    .optional()
    .describe("Format of dataFile (default from the extension: .csv, .jsonl/.ndjson, else json)"),
};

export interface BulkInputArgs<T> {
  items?: T[];
  dataFile?: string;
  dataFormat?: DataFormat;
}

export interface BulkOutcome {
  succeeded: number;
  failed: number;
  results: unknown[];
}

function inferFormat(file: string): DataFormat {
  const ext = path.extname(file).toLowerCase();
  if (ext === ".csv") return "csv";
  if (ext === ".jsonl" || ext === ".ndjson") return "jsonl";
  return "json";
}

/** Inline items first, then the items read from `dataFile`. */
export async function loadItems<T>(args: BulkInputArgs<T>, itemsKey: string): Promise<T[]> {
  const inline = args.items ?? [];
  if (!args.dataFile) return [...inline];

  const file = args.dataFile;
  if (!path.isAbsolute(file)) {
    throw new Error(`dataFile must be an absolute path, got '${file}'`);
  }
  let text: string;
  try {
    text = await fs.readFile(file, "utf8");
  } catch (error) {
    throw new Error(`Cannot read dataFile '${file}': ${error instanceof Error ? error.message : String(error)}`);
  }
  text = text.replace(/^\uFEFF/, "");

  const format = args.dataFormat ?? inferFormat(file);
  let fileItems: unknown[];
  if (format === "csv") fileItems = parseCsvItems(text);
  else if (format === "jsonl") fileItems = parseJsonLines(text, file);
  else fileItems = parseJsonItems(text, file, itemsKey);
  return [...inline, ...(fileItems as T[])];
}

function parseJsonItems(text: string, file: string, itemsKey: string): unknown[] {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch (error) {
    throw new Error(`dataFile '${file}' is not valid JSON: ${error instanceof Error ? error.message : String(error)}`);
  }
  if (Array.isArray(parsed)) return parsed;
  if (parsed && typeof parsed === "object" && Array.isArray((parsed as Record<string, unknown>)[itemsKey])) {
    return (parsed as Record<string, unknown[]>)[itemsKey];
  }
  throw new Error(`dataFile '${file}' must hold a JSON array or an object with a '${itemsKey}' array`);
}

function parseJsonLines(text: string, file: string): unknown[] {
  const items: unknown[] = [];
  text.split(/\r?\n/).forEach((line, index) => {
    if (!line.trim()) return;
    try {
      items.push(JSON.parse(line));
    } catch (error) {
      throw new Error(
        `dataFile '${file}' line ${index + 1} is not valid JSON: ${error instanceof Error ? error.message : String(error)}`
      );
    }
  });
  return items;
}

/** RFC 4180 rows: quoted fields may hold commas, doubled quotes and line breaks. */
function parseCsvRows(text: string): string[][] {
  const rows: string[][] = [];
  let row: string[] = [];
  let field = "";
  let quoted = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c === '"') {
        if (text[i + 1] === '"') {
          field += '"';
          i++;
        } else {
          quoted = false;
        }
      } else {
        field += c;
      }
    } else if (c === '"') {
      quoted = true;
    } else if (c === ",") {
      row.push(field);
      field = "";
    } else if (c === "\n" || c === "\r") {
      if (c === "\r" && text[i + 1] === "\n") i++;
      row.push(field);
      rows.push(row);
      row = [];
      field = "";
    } else {
      field += c;
    }
  }
  if (field !== "" || row.length > 0) {
    row.push(field);
    rows.push(row);
  }
  return rows.filter((r) => r.some((cell) => cell.trim() !== ""));
}

const NUMBER = /^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$/;
const LEADING_ZERO = /^[+-]?0\d/;

function parseCell(raw: string): unknown {
  const value = raw.trim();
  if (NUMBER.test(value) && !LEADING_ZERO.test(value)) return Number(value);
  if (/^true$/i.test(value)) return true;
  if (/^false$/i.test(value)) return false;
  return value;
}

function setPath(target: Record<string, unknown>, keys: string[], value: unknown) {
  let node = target;
  for (const key of keys.slice(0, -1)) {
    if (typeof node[key] !== "object" || node[key] === null) node[key] = {};
    node = node[key] as Record<string, unknown>;
  }
  node[keys[keys.length - 1]] = value;
}

function parseCsvItems(text: string): unknown[] {
  const [header, ...rows] = parseCsvRows(text);
  if (!header) return [];
  const columns = header.map((name) => name.trim().split("."));
  return rows.map((cells) => {
    const item: Record<string, unknown> = {};
    columns.forEach((keys, index) => {
      const raw = cells[index];
      if (!keys[0] || raw === undefined || raw.trim() === "") return;
      setPath(item, keys, parseCell(raw));
    });
    return item;
  });
}

interface ChunkResponse {
  Success?: boolean;
  success?: boolean;
  Message?: string;
  message?: string;
  Response?: { succeeded?: number; failed?: number; results?: unknown[] };
  response?: { succeeded?: number; failed?: number; results?: unknown[] };
}

function failedItems(offset: number, count: number, message: string) {
  return Array.from({ length: count }, (_, i) => ({ index: offset + i, success: false, message }));
}

/**
 * Sends `items` to a Revit batch command in chunks of `chunkSize`, each as
 * `{ ...base, [itemsKey]: chunk }`, and merges the per-chunk
 * `{ succeeded, failed, results }` with `results[].index` made global. A chunk
 * that fails as a whole marks its items failed and stops the remaining chunks.
 */
export async function sendInChunks(
  command: string,
  base: object,
  itemsKey: string,
  items: unknown[],
  chunkSize = 300
): Promise<BulkOutcome> {
  const results: unknown[] = [];
  let succeeded = 0;
  let failed = 0;
  const size = Math.max(1, Math.floor(chunkSize));

  for (let offset = 0; offset < items.length; offset += size) {
    const chunk = items.slice(offset, offset + size);
    let error: string | undefined;
    try {
      const response = (await withRevitConnection((client) =>
        client.sendCommand(command, { ...base, [itemsKey]: chunk })
      )) as ChunkResponse | null;
      const body = response?.Response ?? response?.response;
      if (response?.Success === false || response?.success === false || !Array.isArray(body?.results)) {
        error = response?.Message ?? response?.message ?? "Revit returned no results";
      } else {
        for (const result of body!.results!) {
          const record = result as Record<string, unknown>;
          const index = typeof record.index === "number" ? record.index + offset : undefined;
          results.push(index === undefined ? record : { ...record, index });
          if (record.success === false) failed++;
          else succeeded++;
        }
      }
    } catch (caught) {
      error = caught instanceof Error ? caught.message : String(caught);
    }

    if (error !== undefined) {
      results.push(...failedItems(offset, chunk.length, `${command} chunk failed: ${error}`));
      failed += chunk.length;
      const rest = items.length - offset - chunk.length;
      if (rest > 0) {
        results.push(...failedItems(offset + chunk.length, rest, "Not sent: an earlier chunk failed"));
        failed += rest;
      }
      break;
    }
  }

  return { succeeded, failed, results };
}

/** MCP result for a bulk outcome; an error only when nothing succeeded. */
export function formatBulkResult(command: string, outcome: BulkOutcome): CallToolResult {
  const total = outcome.succeeded + outcome.failed;
  const revitResult = {
    Success: outcome.succeeded > 0 || total === 0,
    Message: `${command}: ${outcome.succeeded} of ${total} items succeeded.`,
    Response: outcome,
  };
  const text = total > 50 ? JSON.stringify(revitResult) : JSON.stringify(revitResult, null, 2);
  // applyBatchOutcome adds the WARNING/ERROR line and sets isError when nothing succeeded.
  return applyBatchOutcome(revitResult, { content: [{ type: "text" as const, text }] });
}
