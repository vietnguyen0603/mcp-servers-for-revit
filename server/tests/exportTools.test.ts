import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { registerExportSheetsTool } from "../src/tools/export_sheets.js";
import { registerExportViewImageTool } from "../src/tools/export_view_image.js";
import { resetConnectionMock, sendCommand, withRevitConnection } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, tools, getTool } = createFakeMcpServer();
  registerExportSheetsTool(server);
  registerExportViewImageTool(server);
  return { tools, getTool };
}

describe("export tools", () => {
  beforeEach(() => {
    resetConnectionMock();
  });

  it("registers export_sheets and export_view_image", () => {
    const { tools } = setup();
    expect([...tools.keys()].sort()).toEqual(["export_sheets", "export_view_image"]);
  });

  it("requires an absolute Windows folder", () => {
    const { getTool } = setup();
    const tool = getTool("export_sheets");
    const base = { format: "PDF", sheetIds: [1] };

    expect(() => tool.parse({ ...base, folder: "exports" })).toThrow(/absolute/);
    expect(() => tool.parse({ ...base, folder: "/tmp/exports" })).toThrow(/absolute/);
    expect(() => tool.parse({ ...base, folder: "C:\\Exports\\a|b" })).toThrow(/invalid path/);
    expect(tool.parse({ ...base, folder: "C:\\Exports\\Issue 01" })).toBeTruthy();
    expect(tool.parse({ ...base, folder: "D:/Exports" })).toBeTruthy();
    expect(tool.parse({ ...base, folder: "\\\\server\\share\\pdf" })).toBeTruthy();
  });

  it("dispatches export_sheets with defaults and validates options", async () => {
    const { getTool } = setup();
    const tool = getTool("export_sheets");
    sendCommand.mockResolvedValue({ success: true, message: "ok", response: {} });

    await tool.invoke({ format: "DWG", sheetNumbers: ["S-101"], folder: "C:\\Out", dwg: { mergedViews: true } });
    expect(sendCommand).toHaveBeenCalledWith("export_sheets", {
      format: "DWG",
      sheetNumbers: ["S-101"],
      folder: "C:\\Out",
      combine: false,
      fileNameTemplate: "{number} - {name}",
      dwg: { mergedViews: true },
    });

    expect(() => tool.parse({ format: "DXF", sheetIds: [1], folder: "C:\\Out" })).toThrow();
    expect(() =>
      tool.parse({ format: "PDF", sheetIds: [1], folder: "C:\\Out", pdf: { zoom: "Half" } })
    ).toThrow();
    expect(tool.parse({ format: "PDF", sheetIds: [1], folder: "C:\\Out", pdf: { zoom: 50 } })).toBeTruthy();
  });

  it("refuses export_sheets without targets before contacting Revit", async () => {
    const { getTool } = setup();
    const result = await getTool("export_sheets").invoke({ format: "PDF", folder: "C:\\Out" });
    expect(result.isError).toBe(true);
    expect(withRevitConnection).not.toHaveBeenCalled();
  });

  it("dispatches export_view_image with defaults and bounds pixelSize", async () => {
    const { getTool } = setup();
    const tool = getTool("export_view_image");
    sendCommand.mockResolvedValue({ success: false, message: "Revit did not write any image files." });

    const result = await tool.invoke({ folder: "C:\\Img" });
    expect(sendCommand).toHaveBeenCalledWith("export_view_image", {
      folder: "C:\\Img",
      format: "PNG",
      pixelSize: 2048,
      fitDirection: "Horizontal",
    });
    expect(result.isError).toBe(true);

    expect(() => tool.parse({ folder: "C:\\Img", pixelSize: 20000 })).toThrow();
    expect(() => tool.parse({ folder: "C:\\Img", format: "GIF" })).toThrow();
  });
});
