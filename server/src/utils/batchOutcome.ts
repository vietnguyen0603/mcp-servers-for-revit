import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

/**
 * Batch command handlers return `{ Success, Message, Response: { succeeded,
 * failed, results } }` and report `Success: true` even when every item
 * failed. These helpers turn such outcomes into something an agent cannot
 * miss: all items failed -> MCP error; some items failed -> a WARNING line.
 */

export interface BatchCounts {
  succeeded: number;
  failed: number;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function readCount(record: Record<string, unknown>, camel: string, pascal: string): number | undefined {
  const value = record[camel] ?? record[pascal];
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

/** Extract numeric succeeded/failed counts from a Revit command result, if present. */
export function getBatchCounts(result: unknown): BatchCounts | undefined {
  if (!isRecord(result)) return undefined;
  const response = result.Response ?? result.response;
  if (!isRecord(response)) return undefined;
  const succeeded = readCount(response, "succeeded", "Succeeded");
  const failed = readCount(response, "failed", "Failed");
  if (succeeded === undefined || failed === undefined) return undefined;
  return { succeeded, failed };
}

/** The line to show for a batch outcome, or undefined when nothing failed. */
export function batchOutcomeLine(counts: BatchCounts): string | undefined {
  if (counts.failed <= 0) return undefined;
  const total = counts.succeeded + counts.failed;
  if (counts.succeeded === 0) {
    return `ERROR: all ${counts.failed} of ${total} items failed — see results[].message`;
  }
  return `WARNING: ${counts.failed} of ${total} items failed — see results[].message`;
}

/**
 * Apply the batch outcome of a Revit result object to an MCP result built
 * from it: prepend the outcome line to the first text item and set
 * `isError` when every item failed.
 */
export function applyBatchOutcome<T extends CallToolResult>(revitResult: unknown, toolResult: T): T {
  if (toolResult.isError) return toolResult;
  const counts = getBatchCounts(revitResult);
  if (!counts) return toolResult;
  const line = batchOutcomeLine(counts);
  if (!line) return toolResult;

  const content = [...toolResult.content];
  const index = content.findIndex((item) => item.type === "text");
  if (index >= 0) {
    const item = content[index] as { type: "text"; text: string };
    content[index] = { ...item, text: `${line}\n${item.text}` };
  } else {
    content.unshift({ type: "text", text: line });
  }
  return {
    ...toolResult,
    content,
    ...(counts.succeeded === 0 ? { isError: true } : {}),
  };
}

/**
 * Inspect an already-rendered MCP tool result whose text content is the
 * JSON-serialised Revit result, and apply the batch outcome. Results that are
 * not JSON, already flagged, or already annotated are returned unchanged.
 */
export function flagBatchFailures<T>(toolResult: T): T {
  if (!isRecord(toolResult) || toolResult.isError || !Array.isArray(toolResult.content)) return toolResult;
  for (const item of toolResult.content as unknown[]) {
    if (!isRecord(item) || item.type !== "text" || typeof item.text !== "string") continue;
    const text = item.text.trimStart();
    if (!text.startsWith("{")) continue;
    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch {
      continue;
    }
    if (getBatchCounts(parsed)) {
      return applyBatchOutcome(parsed, toolResult as unknown as CallToolResult) as unknown as T;
    }
  }
  return toolResult;
}
