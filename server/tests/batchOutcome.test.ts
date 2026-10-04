import { beforeEach, describe, expect, it, vi } from "vitest";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { applyBatchOutcome, flagBatchFailures, getBatchCounts } from "../src/utils/batchOutcome.js";
import { sendDocumentationCommand } from "../src/utils/documentationSchemas.js";
import { ToolCatalog } from "../src/catalog/ToolCatalog.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";

function revitResult(succeeded: number, failed: number) {
  return {
    Success: true,
    Message: `Created ${succeeded} of ${succeeded + failed} regions.`,
    Response: { succeeded, failed, results: [] },
  };
}

function text(result: unknown): string {
  return (result as { content: Array<{ text: string }> }).content[0].text;
}

describe("getBatchCounts", () => {
  it("reads PascalCase and camelCase envelopes", () => {
    expect(getBatchCounts(revitResult(1, 2))).toEqual({ succeeded: 1, failed: 2 });
    expect(getBatchCounts({ success: true, response: { Succeeded: 0, Failed: 1 } })).toEqual({
      succeeded: 0,
      failed: 1,
    });
  });

  it("ignores results without numeric counts", () => {
    expect(getBatchCounts({ Success: true, Response: { results: [] } })).toBeUndefined();
    expect(getBatchCounts({ Success: true, Response: { succeeded: "1", failed: 0 } })).toBeUndefined();
    expect(getBatchCounts(null)).toBeUndefined();
    expect(getBatchCounts([1, 2])).toBeUndefined();
  });
});

describe("applyBatchOutcome", () => {
  const base = () => ({ content: [{ type: "text" as const, text: "{}" }] });

  it("flags all-failed batches as errors", () => {
    const result = applyBatchOutcome(revitResult(0, 1), base());
    expect(result.isError).toBe(true);
    expect(text(result)).toMatch(/^ERROR: all 1 of 1 items failed/);
  });

  it("keeps partial failures successful but prepends a warning", () => {
    const result = applyBatchOutcome(revitResult(3, 2), base());
    expect(result.isError).toBeUndefined();
    expect(text(result)).toBe("WARNING: 2 of 5 items failed — see results[].message\n{}");
  });

  it("leaves clean batches, non-batch results and existing errors alone", () => {
    expect(applyBatchOutcome(revitResult(2, 0), base())).toEqual(base());
    expect(applyBatchOutcome({ Success: true }, base())).toEqual(base());
    const error = { ...base(), isError: true };
    expect(applyBatchOutcome(revitResult(0, 1), error)).toEqual(error);
  });

  it("adds a text item when the result has none", () => {
    const result = applyBatchOutcome(revitResult(1, 1), {
      content: [{ type: "image" as const, data: "", mimeType: "image/png" }],
    });
    expect(result.content[0]).toEqual({ type: "text", text: expect.stringMatching(/^WARNING: 1 of 2/) });
  });
});

describe("flagBatchFailures", () => {
  it("parses JSON text content and applies the outcome", () => {
    const result = flagBatchFailures({ content: [{ type: "text", text: JSON.stringify(revitResult(0, 2)) }] });
    expect(result.isError).toBe(true);
    expect(text(result)).toMatch(/^ERROR: all 2 of 2/);
  });

  it("is idempotent and ignores non-JSON text", () => {
    const once = flagBatchFailures({ content: [{ type: "text", text: JSON.stringify(revitResult(1, 1)) }] });
    expect(flagBatchFailures(once)).toEqual(once);
    const plain = { content: [{ type: "text", text: "hello" }] };
    expect(flagBatchFailures(plain)).toBe(plain);
  });
});

describe("sendDocumentationCommand batch outcomes", () => {
  beforeEach(() => resetConnectionMock());

  it("returns an MCP error when every item failed", async () => {
    sendCommand.mockResolvedValue(revitResult(0, 1));
    const result = await sendDocumentationCommand("create_filled_region", {});
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toMatch(/^ERROR: all 1 of 1 items failed/);
  });

  it("warns on partial failure", async () => {
    sendCommand.mockResolvedValue(revitResult(4, 1));
    const result = await sendDocumentationCommand("create_filled_region", {});
    expect(result.isError).toBeUndefined();
    expect(result.content[0].text.split("\n")[0]).toBe("WARNING: 1 of 5 items failed — see results[].message");
  });
});

describe("ToolCatalog applies batch outcomes to every tool", () => {
  it("flags all-failed batch results from any registered tool", async () => {
    const server = new McpServer({ name: "test", version: "1.0.0" });
    const catalog = new ToolCatalog(server);
    const capturing = catalog.capturingServer();
    capturing.tool("create_dimensions", "Create dimensions.", {}, async () => ({
      content: [{ type: "text" as const, text: JSON.stringify(revitResult(0, 3)) }],
    }));
    catalog.finalize({ mode: "all", preload: [] });

    const client = new Client({ name: "test-client", version: "1.0.0" });
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    await Promise.all([client.connect(clientTransport), server.connect(serverTransport)]);

    const result = (await client.callTool({ name: "create_dimensions", arguments: {} })) as {
      content: Array<{ text: string }>;
      isError?: boolean;
    };
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toMatch(/^ERROR: all 3 of 3 items failed/);
  });
});
