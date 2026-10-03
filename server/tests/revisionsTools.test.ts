import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerCreateRevisionTool } from "../src/tools/create_revision.js";
import { registerListRevisionsTool } from "../src/tools/list_revisions.js";
import { registerUpdateSheetsTool } from "../src/tools/update_sheets.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, tools, getTool } = createFakeMcpServer();
  registerListRevisionsTool(server);
  registerCreateRevisionTool(server);
  registerUpdateSheetsTool(server);
  return { tools, getTool };
}

describe("revision and sheet update tools", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ success: true, message: "ok", response: {} });
  });

  it("registers in the views catalog with the right read-only flags", () => {
    const { tools } = setup();
    expect([...tools.keys()].sort()).toEqual(["create_revision", "list_revisions", "update_sheets"]);
    expect(TOOL_MANIFEST.list_revisions).toMatchObject({ catalogs: ["views"], readOnly: true });
    expect(TOOL_MANIFEST.create_revision).toMatchObject({ catalogs: ["views"], readOnly: false });
    expect(TOOL_MANIFEST.update_sheets).toMatchObject({ catalogs: ["views"], readOnly: false });
  });

  it("dispatches each tool under its own command name", async () => {
    const { getTool } = setup();

    await getTool("list_revisions").invoke({});
    expect(sendCommand).toHaveBeenLastCalledWith("list_revisions", {});

    const revisions = [{ description: "Issued for construction", date: "2026-10-02", issued: true }];
    await getTool("create_revision").invoke({ revisions });
    expect(sendCommand).toHaveBeenLastCalledWith("create_revision", { revisions });

    const sheets = [{ sheetNumber: "S-101", newNumber: "S-102", addRevisionIds: [12] }];
    await getTool("update_sheets").invoke({ sheets });
    expect(sendCommand).toHaveBeenLastCalledWith("update_sheets", { sheets });
  });

  it("validates create_revision input", () => {
    const { getTool } = setup();
    const tool = getTool("create_revision");
    expect(() => tool.parse({ revisions: [] })).toThrow();
    expect(() => tool.parse({ revisions: [{ description: "A", visibility: "Visible" }] })).toThrow();
    expect(() => tool.parse({ revisions: [{ date: "2026-10-02" }] })).toThrow();
    expect(tool.parse({ revisions: [{ description: "A", visibility: "TagVisible" }] })).toBeTruthy();
  });

  it("requires a sheet identifier and positive revision ids in update_sheets", () => {
    const { getTool } = setup();
    const tool = getTool("update_sheets");
    expect(() => tool.parse({ sheets: [{ newName: "Plan" }] })).toThrow(/sheetId or sheetNumber/);
    expect(() => tool.parse({ sheets: [{ sheetId: 5, addRevisionIds: [0] }] })).toThrow();
    expect(tool.parse({ sheets: [{ sheetId: 5, parameters: { "Drawn By": "VN", Scale: 100 } }] })).toBeTruthy();
  });

  it("surfaces Revit-side failures as tool errors", async () => {
    const { getTool } = setup();
    sendCommand.mockResolvedValueOnce({ success: false, message: "No active Revit document." });
    const result = await getTool("list_revisions").invoke({});
    expect(result.isError).toBe(true);
  });
});
