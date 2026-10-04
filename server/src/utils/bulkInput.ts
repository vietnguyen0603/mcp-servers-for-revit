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
  summary: z
    .boolean()
    .optional()
    .describe(
      "Return only totals, failed items grouped by error (with their indexes), distinct warnings with counts and the created ids as compact ranges - no per-item records. Use for large batches; a result over ~60 kB switches to the summary automatically"
    ),
};

export interface BulkInputArgs<T> {
  items?: T[];
  dataFile?: string;
  dataFormat?: DataFormat;
  summary?: boolean;
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

/** Full results longer than this (characters) are replaced by the summary. */
export const MAX_FULL_RESULT_CHARS = 60_000;

const MAX_SUMMARY_GROUPS = 50;
const MAX_ID_RANGES = 200;

export interface BulkSummary {
  succeeded: number;
  failed: number;
  total: number;
  summary: true;
  /** Created element ids in item order: "first-last" runs of consecutive ids, or first/last when there are too many runs. */
  createdIds: { count: number; ranges?: string; first?: number; last?: number };
  /** Failed items grouped by message; `indexes` as compact ranges ("0-4, 9"). */
  failures: Array<{ message: string; count: number; indexes: string }>;
  /** Distinct warnings with how many items reported each and the first item indexes. */
  warnings: Array<{ message: string; count: number; indexes: string }>;
  note?: string;
}

/** "0-4, 7, 9-10" from integers; consecutive (step 1) runs collapse. Input order is kept. */
export function compactRanges(values: number[]): string {
  const parts: string[] = [];
  let start: number | undefined;
  let previous: number | undefined;
  const flush = () => {
    if (start === undefined || previous === undefined) return;
    parts.push(start === previous ? `${start}` : `${start}-${previous}`);
  };
  for (const value of values) {
    if (previous !== undefined && value === previous + 1) {
      previous = value;
      continue;
    }
    flush();
    start = previous = value;
  }
  flush();
  return parts.join(", ");
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** Element ids a result record reports as created: id, ids[], else typeId (create_family_type). */
function createdIdsOf(record: Record<string, unknown>): number[] {
  if (typeof record.id === "number") return [record.id];
  if (Array.isArray(record.ids)) return record.ids.filter((v): v is number => typeof v === "number");
  if (typeof record.typeId === "number" && record.id === undefined) return [record.typeId];
  return [];
}

function groupList(groups: Map<string, number[]>, label: string) {
  const list = [...groups.entries()]
    .sort((a, b) => b[1].length - a[1].length)
    .map(([message, indexes]) => ({ message, count: indexes.length, indexes: compactRanges(indexes) }));
  if (list.length <= MAX_SUMMARY_GROUPS) return list;
  const rest = list.slice(MAX_SUMMARY_GROUPS);
  return [
    ...list.slice(0, MAX_SUMMARY_GROUPS),
    {
      message: `... ${rest.length} more distinct ${label}`,
      count: rest.reduce((sum, g) => sum + g.count, 0),
      indexes: "",
    },
  ];
}

/** Compact digest of a bulk outcome: no per-item records. */
export function summarizeBulkOutcome(outcome: BulkOutcome): BulkSummary {
  const failures = new Map<string, number[]>();
  const warnings = new Map<string, number[]>();
  const ids: number[] = [];
  outcome.results.forEach((result, position) => {
    if (!isRecord(result)) return;
    const index = typeof result.index === "number" ? result.index : position;
    if (result.success === false) {
      const message = typeof result.message === "string" ? result.message : "Failed";
      const list = failures.get(message) ?? [];
      list.push(index);
      failures.set(message, list);
      return;
    }
    ids.push(...createdIdsOf(result));
    if (Array.isArray(result.warnings)) {
      for (const warning of new Set(result.warnings.map((w) => String(w)))) {
        const list = warnings.get(warning) ?? [];
        list.push(index);
        warnings.set(warning, list);
      }
    }
  });

  const ranges = compactRanges(ids);
  const runCount = ranges === "" ? 0 : ranges.split(", ").length;
  const createdIds =
    ids.length === 0
      ? { count: 0 }
      : runCount <= MAX_ID_RANGES
        ? { count: ids.length, ranges }
        : { count: ids.length, first: ids[0], last: ids[ids.length - 1] };

  const warningGroups = groupList(warnings, "warnings").map((group) => ({
    ...group,
    // Keep the index list short for common warnings.
    indexes: group.indexes.length > 200 ? `${group.indexes.slice(0, 200)}...` : group.indexes,
  }));

  return {
    succeeded: outcome.succeeded,
    failed: outcome.failed,
    total: outcome.succeeded + outcome.failed,
    summary: true,
    createdIds,
    failures: groupList(failures, "errors"),
    warnings: warningGroups,
  };
}

export interface FormatBulkOptions {
  /** Return the summary instead of per-item results. */
  summary?: boolean;
  /** Switch to the summary when the full text exceeds this many characters (default MAX_FULL_RESULT_CHARS). */
  maxChars?: number;
}

/** MCP result for a bulk outcome; an error only when nothing succeeded. */
export function formatBulkResult(command: string, outcome: BulkOutcome, options: FormatBulkOptions = {}): CallToolResult {
  const total = outcome.succeeded + outcome.failed;
  const message = `${command}: ${outcome.succeeded} of ${total} items succeeded.`;
  const success = outcome.succeeded > 0 || total === 0;

  let text: string | undefined;
  let note: string | undefined;
  if (!options.summary) {
    const full = { Success: success, Message: message, Response: outcome };
    text = total > 50 ? JSON.stringify(full) : JSON.stringify(full, null, 2);
    const limit = options.maxChars ?? MAX_FULL_RESULT_CHARS;
    if (text.length > limit) {
      note =
        `Per-item results omitted: the full result is ${Math.round(text.length / 1024)} kB (limit ${Math.round(limit / 1024)} kB). ` +
        "Use the created id ranges, or re-run smaller batches for per-item records.";
      text = undefined;
    }
  }

  const revitResult = text !== undefined
    ? { Success: success, Message: message, Response: outcome }
    : {
        Success: success,
        Message: message,
        Response: note ? { ...summarizeBulkOutcome(outcome), note } : summarizeBulkOutcome(outcome),
      };
  const summarized = text === undefined;
  if (summarized) text = JSON.stringify(revitResult, null, 2);
  // applyBatchOutcome adds the WARNING/ERROR line and sets isError when nothing succeeded.
  const result = applyBatchOutcome(revitResult, { content: [{ type: "text" as const, text: text! }] });
  if (summarized && result.content[0]?.type === "text") {
    const first = result.content[0] as { type: "text"; text: string };
    result.content[0] = { ...first, text: first.text.replace("see results[].message", "see failures[]") };
  }
  return result;
}
