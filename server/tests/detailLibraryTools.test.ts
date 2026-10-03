import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { registerAuditDetailReferencesTool } from "../src/tools/audit_detail_references.js";
import { registerSyncDetailReferencesTool } from "../src/tools/sync_detail_references.js";
import { registerCreateViewReferenceTool } from "../src/tools/create_view_reference.js";
import { registerCopyViewContentsTool } from "../src/tools/copy_view_contents.js";
import { registerCopyDraftingViewsTool } from "../src/tools/copy_drafting_views.js";
import { registerLayoutDetailSheetTool } from "../src/tools/layout_detail_sheet.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerAuditDetailReferencesTool(server);
  registerSyncDetailReferencesTool(server);
  registerCreateViewReferenceTool(server);
  registerCopyViewContentsTool(server);
  registerCopyDraftingViewsTool(server);
  registerLayoutDetailSheetTool(server);
  return getTool;
}

describe("detail library tools", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: {} });
  });

  it("dispatches audit_detail_references with paging defaults", async () => {
    const tool = setup()("audit_detail_references");
    await tool.invoke({ sheetNumbers: ["S-501"], placeholders: ["", "-", "S-000"] });
    expect(sendCommand).toHaveBeenCalledWith("audit_detail_references", {
      sheetNumbers: ["S-501"],
      placeholders: ["", "-", "S-000"],
      includeOk: false,
      limit: 500,
      offset: 0,
    });
    expect(tool.parse({ familyNameContains: "Section Cut", detailNumberParam: "Detail Number" })).toBeTruthy();
    expect(() => tool.parse({ limit: 0 })).toThrow();
    expect(() => tool.parse({ viewIds: [-1] })).toThrow();
    expect(() => tool.parse({ familyNameContains: "" })).toThrow();
  });

  it("validates sync_detail_references modes before calling Revit", async () => {
    const tool = setup()("sync_detail_references");

    const empty = await tool.invoke({});
    expect(empty.isError).toBe(true);
    expect(empty.content[0].text).toMatch(/mappings/);

    const half = await tool.invoke({ renumberFrom: { sheetNumber: "S-501", detailNumber: "3" } });
    expect(half.isError).toBe(true);
    expect(sendCommand).not.toHaveBeenCalled();

    await tool.invoke({ mappings: [{ bubbleIds: [11, 12], targetViewId: 99 }] });
    expect(sendCommand).toHaveBeenCalledWith("sync_detail_references", {
      mappings: [{ bubbleIds: [11, 12], targetViewId: 99 }],
      dryRun: true,
    });

    await tool.invoke({
      renumberFrom: { sheetNumber: "S-501", detailNumber: "3" },
      to: { sheetNumber: "S-502", detailNumber: "7" },
      dryRun: false,
    });
    expect(sendCommand).toHaveBeenLastCalledWith("sync_detail_references", {
      renumberFrom: { sheetNumber: "S-501", detailNumber: "3" },
      to: { sheetNumber: "S-502", detailNumber: "7" },
      dryRun: false,
    });

    expect(() => tool.parse({ mappings: [{ bubbleIds: [], targetViewId: 1 }] })).toThrow();
    expect(() => tool.parse({ renumberFrom: { sheetNumber: "S-1" } })).toThrow();
  });

  it("validates create_view_reference kinds", async () => {
    const tool = setup()("create_view_reference");
    const parsed = tool.parse({
      references: [{ parentViewId: 1, targetViewId: 2, start: { x: 0, y: 0 }, end: { x: 500, y: 300 } }],
    });
    expect((parsed.references as Array<{ kind: string }>)[0].kind).toBe("callout");
    expect(() =>
      tool.parse({
        references: [{ parentViewId: 1, targetViewId: 2, kind: "viewReference", start: { x: 0, y: 0 }, end: { x: 1, y: 1 } }],
      })
    ).toThrow();
    expect(() => tool.parse({ references: [{ parentViewId: 1, targetViewId: 2, start: { x: 0, y: 0 } }] })).toThrow();
    expect(() => tool.parse({ references: [] })).toThrow();
  });

  it("dispatches copy_view_contents and copy_drafting_views", async () => {
    const getTool = setup();
    await getTool("copy_view_contents").invoke({
      sourceViewId: 10,
      targetViewId: 20,
      offset: { x: 100, y: -50 },
      sourceDocumentTitle: "Typical Details.rvt",
    });
    expect(sendCommand).toHaveBeenCalledWith("copy_view_contents", {
      sourceViewId: 10,
      targetViewId: 20,
      offset: { x: 100, y: -50 },
      sourceDocumentTitle: "Typical Details.rvt",
    });
    expect(() => getTool("copy_view_contents").parse({ sourceViewId: 10 })).toThrow();

    const drafting = getTool("copy_drafting_views");
    const missing = await drafting.invoke({ sourceDocumentTitle: "Library" });
    expect(missing.isError).toBe(true);

    await drafting.invoke({ sourceDocumentTitle: "Library", viewNames: ["FN-SF-03"] });
    expect(sendCommand).toHaveBeenLastCalledWith("copy_drafting_views", {
      sourceDocumentTitle: "Library",
      viewNames: ["FN-SF-03"],
      nameConflict: "skip",
    });
    expect(() => drafting.parse({ sourceDocumentTitle: "Library", viewIds: [1], nameConflict: "overwrite" })).toThrow();
    expect(() => drafting.parse({ viewIds: [1] })).toThrow();
  });

  it("applies layout_detail_sheet defaults and validates the grid", async () => {
    const tool = setup()("layout_detail_sheet");
    await tool.invoke({ sheetId: 5, viewIds: [1, 2, 3], columns: 5, rows: 4 });
    expect(sendCommand).toHaveBeenCalledWith("layout_detail_sheet", {
      sheetId: 5,
      viewIds: [1, 2, 3],
      columns: 5,
      rows: 4,
      order: "rightToLeftTopDown",
      startDetailNumber: 1,
      numbering: true,
      dryRun: false,
    });

    expect(
      tool.parse({
        sheetId: 5,
        viewIds: [1],
        region: { min: { x: 20, y: 20 }, max: { x: 950, y: 740 } },
        margins: { right: 120 },
        order: "leftToRightTopDown",
        dryRun: true,
      })
    ).toBeTruthy();
    expect(() => tool.parse({ sheetId: 5, viewIds: [] })).toThrow();
    expect(() => tool.parse({ sheetId: 5, viewIds: [1], columns: 0 })).toThrow();
    expect(() => tool.parse({ sheetId: 5, viewIds: [1], order: "bottomUp" })).toThrow();
    expect(() => tool.parse({ sheetId: 5, viewIds: [1], margins: { middle: 3 } })).toThrow();
  });
});
