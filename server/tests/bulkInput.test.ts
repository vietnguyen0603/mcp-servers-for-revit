import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { mkdtemp, rm, writeFile } from "fs/promises";
import os from "os";
import path from "path";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import {
  bulkInputShape,
  compactRanges,
  formatBulkResult,
  loadItems,
  sendInChunks,
  summarizeBulkOutcome,
} from "../src/utils/bulkInput.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";

let dir: string;

beforeAll(async () => {
  dir = await mkdtemp(path.join(os.tmpdir(), "bulk-input-"));
});

afterAll(async () => {
  await rm(dir, { recursive: true, force: true });
});

async function file(name: string, content: string) {
  const full = path.join(dir, name);
  await writeFile(full, content, "utf8");
  return full;
}

describe("loadItems", () => {
  it("returns inline items when there is no file", async () => {
    expect(await loadItems({ items: [{ a: 1 }] }, "beams")).toEqual([{ a: 1 }]);
    expect(await loadItems({}, "beams")).toEqual([]);
  });

  it("reads a JSON array or an object with the items key, after inline items", async () => {
    const array = await file("array.json", JSON.stringify([{ x: 1 }, { x: 2 }]));
    expect(await loadItems({ items: [{ x: 0 }], dataFile: array }, "columns")).toEqual([{ x: 0 }, { x: 1 }, { x: 2 }]);
    const wrapped = await file("wrapped.json", "\uFEFF" + JSON.stringify({ columns: [{ x: 3 }] }));
    expect(await loadItems({ dataFile: wrapped }, "columns")).toEqual([{ x: 3 }]);
    await expect(loadItems({ dataFile: wrapped }, "beams")).rejects.toThrow(/'beams' array/);
  });

  it("reads JSON Lines by extension or explicit format", async () => {
    const lines = await file("items.jsonl", '{"x":1}\n\n{"x":2}\r\n');
    expect(await loadItems({ dataFile: lines }, "beams")).toEqual([{ x: 1 }, { x: 2 }]);
    const txt = await file("items.txt", '{"x":5}\n');
    expect(await loadItems({ dataFile: txt, dataFormat: "jsonl" }, "beams")).toEqual([{ x: 5 }]);
    const bad = await file("bad.jsonl", '{"x":1}\n{oops\n');
    await expect(loadItems({ dataFile: bad }, "beams")).rejects.toThrow(/line 2/);
  });

  it("reads CSV with nested dotted headers, numbers, booleans, quotes and empty cells", async () => {
    const csv = await file(
      "beams.csv",
      [
        "typeId,start.x,start.y,end.x,end.y,level,mark,disallowJoin,comments",
        '123,0,0,6000,0,Level 2,B-1,true,"girder, grid A"',
        '123,-1.5e3,0.5,6000,8000,0012,,FALSE,"say ""hi"""',
        "",
      ].join("\r\n")
    );
    expect(await loadItems({ dataFile: csv }, "beams")).toEqual([
      {
        typeId: 123,
        start: { x: 0, y: 0 },
        end: { x: 6000, y: 0 },
        level: "Level 2",
        mark: "B-1",
        disallowJoin: true,
        comments: "girder, grid A",
      },
      {
        typeId: 123,
        start: { x: -1500, y: 0.5 },
        end: { x: 6000, y: 8000 },
        level: "0012",
        disallowJoin: false,
        comments: 'say "hi"',
      },
    ]);
  });

  it("rejects relative or unreadable files with a clear message", async () => {
    await expect(loadItems({ dataFile: "beams.csv" }, "beams")).rejects.toThrow(/absolute path/);
    await expect(loadItems({ dataFile: path.join(dir, "missing.json") }, "beams")).rejects.toThrow(
      /Cannot read dataFile/
    );
    const invalid = await file("invalid.json", "{");
    await expect(loadItems({ dataFile: invalid }, "beams")).rejects.toThrow(/not valid JSON/);
  });
});

describe("sendInChunks", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockImplementation(async (_command: string, params: { beams: unknown[] }) => ({
      Success: true,
      Message: "ok",
      Response: {
        succeeded: params.beams.length - 1,
        failed: 1,
        results: params.beams.map((_, index) => ({ index, success: index !== 0, id: 1000 + index })),
      },
    }));
  });

  it("sends chunks, re-indexes results globally and sums the counts", async () => {
    const items = Array.from({ length: 7 }, (_, i) => ({ i }));
    const outcome = await sendInChunks("create_beams", { extra: true }, "beams", items, 3);
    expect(sendCommand).toHaveBeenCalledTimes(3);
    expect(sendCommand).toHaveBeenNthCalledWith(2, "create_beams", { extra: true, beams: items.slice(3, 6) });
    expect(outcome.succeeded).toBe(4);
    expect(outcome.failed).toBe(3);
    expect((outcome.results as Array<{ index: number }>).map((r) => r.index)).toEqual([0, 1, 2, 3, 4, 5, 6]);
    expect(outcome.results[4]).toMatchObject({ index: 4, success: true, id: 1001 });
  });

  it("marks a failed chunk and stops sending the rest", async () => {
    sendCommand
      .mockResolvedValueOnce({ Success: true, Response: { succeeded: 2, failed: 0, results: [{ index: 0, success: true }, { index: 1, success: true }] } })
      .mockRejectedValueOnce(new Error("Command timed out"));
    const outcome = await sendInChunks("create_beams", {}, "beams", [1, 2, 3, 4, 5], 2);
    expect(sendCommand).toHaveBeenCalledTimes(2);
    expect(outcome).toMatchObject({ succeeded: 2, failed: 3 });
    expect(outcome.results[2]).toMatchObject({ index: 2, success: false, message: expect.stringMatching(/timed out/) });
    expect(outcome.results[4]).toMatchObject({ index: 4, success: false, message: expect.stringMatching(/Not sent/) });
  });

  it("treats Success:false as a failed chunk", async () => {
    sendCommand.mockResolvedValueOnce({ Success: false, Message: "No active Revit document." });
    const outcome = await sendInChunks("create_beams", {}, "beams", [1, 2], 300);
    expect(outcome).toMatchObject({ succeeded: 0, failed: 2 });
    expect(outcome.results[0]).toMatchObject({ message: expect.stringMatching(/No active Revit document/) });
  });
});

describe("formatBulkResult", () => {
  it("is an error only when nothing succeeded", () => {
    const partial = formatBulkResult("create_beams", { succeeded: 1, failed: 1, results: [] });
    expect(partial.isError).toBeUndefined();
    expect((partial.content[0] as { text: string }).text).toMatch(/^WARNING: 1 of 2/);
    const none = formatBulkResult("create_beams", { succeeded: 0, failed: 2, results: [] });
    expect(none.isError).toBe(true);
    const all = formatBulkResult("create_beams", { succeeded: 2, failed: 0, results: [] });
    expect(all.isError).toBeUndefined();
    expect(JSON.parse((all.content[0] as { text: string }).text)).toMatchObject({ Success: true, Response: { succeeded: 2 } });
  });
});

describe("bulk summary", () => {
  const outcome = {
    succeeded: 5,
    failed: 3,
    results: [
      { index: 0, success: true, id: 100, warnings: ["Joined to wall", "Joined to wall"] },
      { index: 1, success: true, id: 101 },
      { index: 2, success: false, message: "Level 'X' not found." },
      { index: 3, success: true, id: 102, warnings: ["Joined to wall"] },
      { index: 4, success: false, message: "Level 'X' not found." },
      { index: 5, success: true, id: 200, warnings: ["Could not set mark"] },
      { index: 6, success: true, ids: [300, 301] },
      { index: 7, success: false, message: "Bad curve" },
    ],
  };

  it("compacts integers into ranges in order", () => {
    expect(compactRanges([])).toBe("");
    expect(compactRanges([1, 2, 3, 5, 7, 8])).toBe("1-3, 5, 7-8");
    expect(compactRanges([9, 4, 5])).toBe("9, 4-5");
  });

  it("groups failures and warnings and compacts created ids", () => {
    const summary = summarizeBulkOutcome(outcome);
    expect(summary).toMatchObject({ succeeded: 5, failed: 3, total: 8, summary: true });
    expect(summary.createdIds).toEqual({ count: 6, ranges: "100-102, 200, 300-301" });
    expect(summary.failures).toEqual([
      { message: "Level 'X' not found.", count: 2, indexes: "2, 4" },
      { message: "Bad curve", count: 1, indexes: "7" },
    ]);
    expect(summary.warnings).toEqual([
      { message: "Joined to wall", count: 2, indexes: "0, 3" },
      { message: "Could not set mark", count: 1, indexes: "5" },
    ]);
  });

  it("uses typeId when results carry no element id, and first/last for scattered ids", () => {
    const types = summarizeBulkOutcome({ succeeded: 1, failed: 0, results: [{ index: 0, success: true, typeId: 55 }] });
    expect(types.createdIds).toEqual({ count: 1, ranges: "55" });
    const scattered = Array.from({ length: 500 }, (_, i) => ({ index: i, success: true, id: 1000 + i * 3 }));
    const summary = summarizeBulkOutcome({ succeeded: 500, failed: 0, results: scattered });
    expect(summary.createdIds).toEqual({ count: 500, first: 1000, last: 1000 + 499 * 3 });
  });

  it("returns only the summary when asked", () => {
    const result = formatBulkResult("create_walls", outcome, { summary: true });
    const text = (result.content[0] as { text: string }).text;
    const newline = text.indexOf("\n");
    expect(text.slice(0, newline)).toBe("WARNING: 3 of 8 items failed — see failures[]");
    const body = JSON.parse(text.slice(newline + 1));
    expect(body.Response.results).toBeUndefined();
    expect(body.Response).toMatchObject({ summary: true, total: 8, createdIds: { count: 6 } });
    expect(body.Response.note).toBeUndefined();
  });

  it("falls back to the summary when the full result is too large", () => {
    const results = Array.from({ length: 2000 }, (_, i) => ({
      index: i,
      success: true,
      id: 5000 + i,
      typeId: 77,
      level: "Level 12",
      startOffset: -150,
      endOffset: -150,
      length: 8400,
    }));
    const big = { succeeded: 2000, failed: 0, results };
    const result = formatBulkResult("create_beams", big);
    const text = (result.content[0] as { text: string }).text;
    expect(text.length).toBeLessThan(2000);
    const body = JSON.parse(text);
    expect(body.Response).toMatchObject({ summary: true, createdIds: { count: 2000, ranges: "5000-6999" } });
    expect(body.Response.note).toMatch(/Per-item results omitted/);

    const small = formatBulkResult("create_beams", { succeeded: 1, failed: 0, results: [results[0]] });
    expect(JSON.parse((small.content[0] as { text: string }).text).Response.results).toHaveLength(1);
  });

  it("all-failed summaries are errors", () => {
    const result = formatBulkResult(
      "create_beams",
      { succeeded: 0, failed: 1, results: [{ index: 0, success: false, message: "x" }] },
      { summary: true }
    );
    expect(result.isError).toBe(true);
    expect((result.content[0] as { text: string }).text).toMatch(/^ERROR: all 1 of 1 items failed — see failures\[\]/);
  });

  it("is offered by every bulk tool through bulkInputShape", () => {
    expect(bulkInputShape.summary.parse(true)).toBe(true);
    expect(bulkInputShape.summary.parse(undefined)).toBeUndefined();
  });
});
