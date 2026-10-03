/**
 * Helpers for server-side composite tools that issue several existing Revit
 * batch commands over one connection (draw_detail, create_drafted_table).
 *
 * Batch handlers answer `{ Success, Message, Response: { succeeded, failed,
 * results: [{ index, success, message?, ...data }] } }` (Pascal- or
 * camelCase depending on the serialiser); these helpers read either.
 */

export interface RevitSender {
  sendCommand(command: string, params: unknown): Promise<unknown>;
}

export interface ItemOutcome {
  success: boolean;
  message?: string;
  data: Record<string, unknown>;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function pick(record: Record<string, unknown>, camel: string): unknown {
  return record[camel] ?? record[camel.charAt(0).toUpperCase() + camel.slice(1)];
}

/** Unwrap an AIResult-style response: `{ success, message, response }`. */
export function unwrapResult(result: unknown): { success: boolean; message?: string; response: unknown } {
  if (!isRecord(result)) return { success: result != null, response: result };
  const success = pick(result, "success");
  const message = pick(result, "message");
  const hasResponse = "response" in result || "Response" in result;
  return {
    success: success !== false,
    message: typeof message === "string" ? message : undefined,
    response: hasResponse ? pick(result, "response") : result,
  };
}

/**
 * Sends `items` in chunks as `{ ...baseParams, [listKey]: chunk }` and returns
 * one outcome per input item, in order. Transport errors or missing per-item
 * results fail the whole chunk with a message instead of throwing.
 */
export async function runChunkedBatch(
  client: RevitSender,
  command: string,
  listKey: string,
  items: unknown[],
  baseParams: Record<string, unknown> = {},
  chunkSize = 150
): Promise<ItemOutcome[]> {
  const outcomes: ItemOutcome[] = [];
  for (let offset = 0; offset < items.length; offset += chunkSize) {
    const part = items.slice(offset, offset + chunkSize);
    const failAll = (message: string) => part.forEach(() => outcomes.push({ success: false, message, data: {} }));
    let raw: unknown;
    try {
      raw = await client.sendCommand(command, { ...baseParams, [listKey]: part });
    } catch (error) {
      failAll(`${command} failed: ${error instanceof Error ? error.message : String(error)}`);
      continue;
    }
    const { success, message, response } = unwrapResult(raw);
    const results = isRecord(response) ? pick(response, "results") : undefined;
    if (!Array.isArray(results)) {
      failAll(message ?? `${command} returned no per-item results${success ? "" : " (failed)"}`);
      continue;
    }
    const byIndex = new Map<number, Record<string, unknown>>();
    results.forEach((entry, position) => {
      if (!isRecord(entry)) return;
      const index = pick(entry, "index");
      byIndex.set(typeof index === "number" ? index : position, entry);
    });
    part.forEach((_, i) => {
      const entry = byIndex.get(i);
      if (!entry) {
        outcomes.push({ success: false, message: message ?? `${command} returned no result for this item`, data: {} });
        return;
      }
      const itemMessage = pick(entry, "message");
      outcomes.push({
        success: pick(entry, "success") === true,
        message: typeof itemMessage === "string" ? itemMessage : undefined,
        data: entry,
      });
    });
  }
  return outcomes;
}

function toId(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) && value > 0 ? value : undefined;
}

/** Reads a numeric id (or array of ids) from an item result field. */
export function readIds(data: Record<string, unknown>, field: string): number[] {
  const value = pick(data, field);
  if (Array.isArray(value)) return value.map(toId).filter((id): id is number => id !== undefined);
  const id = toId(value);
  return id === undefined ? [] : [id];
}

/** Item-level warnings (string or string[]) reported by a handler. */
export function readWarnings(data: Record<string, unknown>): string[] {
  const value = pick(data, "warnings");
  if (Array.isArray(value)) return value.filter((w): w is string => typeof w === "string");
  return typeof value === "string" ? [value] : [];
}

export interface ViewInfo {
  viewId: number;
  scale?: number;
}

/**
 * Resolves the target view id (active view when omitted) and, when
 * `needScale`, its scale via get_current_view_info / list_views.
 */
export async function resolveViewInfo(client: RevitSender, viewId: number | undefined, needScale: boolean): Promise<ViewInfo> {
  if (viewId === undefined) {
    const { response } = unwrapResult(await client.sendCommand("get_current_view_info", {}));
    if (!isRecord(response)) throw new Error("get_current_view_info returned no view");
    const id = toId(pick(response, "id"));
    if (id === undefined) throw new Error("Could not read the active view id");
    const scale = toId(pick(response, "scale"));
    return { viewId: id, scale };
  }
  if (!needScale) return { viewId };
  const { response } = unwrapResult(await client.sendCommand("list_views", { limit: 5000 }));
  const views = isRecord(response) ? pick(response, "views") : undefined;
  if (Array.isArray(views)) {
    for (const view of views) {
      if (isRecord(view) && pick(view, "id") === viewId) return { viewId, scale: toId(pick(view, "scale")) };
    }
  }
  return { viewId };
}
