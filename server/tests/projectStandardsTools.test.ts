import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { PROJECT_STANDARD_KINDS, registerCopyProjectStandardsTool } from "../src/tools/copy_project_standards.js";
import { PROJECT_STYLE_SECTIONS, registerExportProjectStyleTool } from "../src/tools/export_project_style.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerCopyProjectStandardsTool(server);
  registerExportProjectStyleTool(server);
  return getTool;
}

describe("copy_project_standards", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: { summary: { copied: 1 } } });
  });

  it("forwards kinds with name filters and a long timeout", async () => {
    const tool = setup()("copy_project_standards");
    const args = {
      sourcePath: "C:/Standards/Office.rte",
      kinds: ["viewTemplates", { kind: "textTypes", names: ["2.5mm*"] }, { kind: "tagFamilies", categories: ["OST_StructuralFramingTags"] }],
      onDuplicate: "overwrite",
    };
    await tool.invoke(args);
    expect(sendCommand).toHaveBeenCalledWith("copy_project_standards", args, 600000);
  });

  it("allows listOnly without kinds", async () => {
    const tool = setup()("copy_project_standards");
    await tool.invoke({ sourceDocument: "Office Template", listOnly: true });
    expect(sendCommand).toHaveBeenCalledWith("copy_project_standards", { sourceDocument: "Office Template", listOnly: true }, 600000);
  });

  it("requires exactly one source and something to copy", async () => {
    const tool = setup()("copy_project_standards");
    expect((await tool.invoke({ kinds: ["textTypes"] })).isError).toBe(true);
    expect((await tool.invoke({ sourceDocument: "A", sourcePath: "C:/a.rvt", kinds: ["textTypes"] })).isError).toBe(true);
    expect((await tool.invoke({ sourceDocument: "A" })).isError).toBe(true);
    expect(sendCommand).not.toHaveBeenCalled();
  });

  it("rejects unknown kinds, duplicate modes and stray fields", () => {
    const tool = setup()("copy_project_standards");
    expect(() => tool.parse({ sourceDocument: "A", kinds: ["sheets"] })).toThrow();
    expect(() => tool.parse({ sourceDocument: "A", kinds: ["textTypes"], onDuplicate: "rename" })).toThrow();
    expect(() => tool.parse({ sourceDocument: "A", kinds: [{ kind: "textTypes", name: "x" }] })).toThrow();
    expect(() => tool.parse({ sourceDocument: "A", kinds: [] })).toThrow();
    for (const kind of PROJECT_STANDARD_KINDS) {
      expect(() => tool.parse({ sourceDocument: "A", kinds: [kind] })).not.toThrow();
    }
  });

  it("describes the Transfer Project Standards use case", () => {
    const tool = setup()("copy_project_standards");
    expect(tool.description).toMatch(/Transfer Project Standards/);
    expect(tool.description).toMatch(/listOnly/);
  });
});

describe("export_project_style", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: { outFile: "C:/x.json" } });
  });

  it("exports the active document by default", async () => {
    const tool = setup()("export_project_style");
    await tool.invoke({});
    expect(sendCommand).toHaveBeenCalledWith("export_project_style", {}, 600000);
  });

  it("forwards source, file and sections", async () => {
    const tool = setup()("export_project_style");
    const args = { sourcePath: "C:/Standards/Office.rte", outFile: "C:/Standards/office-style.json", sections: ["viewTemplates", "textTypes"], includeParameters: false };
    await tool.invoke(args);
    expect(sendCommand).toHaveBeenCalledWith("export_project_style", args, 600000);
  });

  it("validates sections and the source", async () => {
    const tool = setup()("export_project_style");
    expect(() => tool.parse({ sections: ["sheets"] })).toThrow();
    expect(() => tool.parse({ sections: [...PROJECT_STYLE_SECTIONS] })).not.toThrow();
    const both = await tool.invoke({ sourceDocument: "A", sourcePath: "C:/a.rvt" });
    expect(both.isError).toBe(true);
    expect(sendCommand).not.toHaveBeenCalled();
  });

  it("uses tool units in the description", () => {
    const tool = setup()("export_project_style");
    expect(tool.description).toMatch(/mm/);
    expect(tool.description).toMatch(/project-style-profile\.md/);
  });
});

describe("project standards manifest", () => {
  it("places both tools in views and annotate", () => {
    expect(TOOL_MANIFEST.copy_project_standards?.catalogs).toEqual(["views", "annotate"]);
    expect(TOOL_MANIFEST.copy_project_standards?.readOnly).toBe(false);
    expect(TOOL_MANIFEST.export_project_style?.catalogs).toEqual(["views", "annotate"]);
    expect(TOOL_MANIFEST.export_project_style?.readOnly).toBe(true);
  });
});
