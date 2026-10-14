import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerGetCadGeometryTool } from "../src/tools/get_cad_geometry.js";
import { registerLinkCadTool } from "../src/tools/link_cad.js";
import { registerPurgeCadLeftoversTool } from "../src/tools/purge_cad_leftovers.js";
import { registerTraceCadImportTool } from "../src/tools/trace_cad_import.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerLinkCadTool(server);
  registerGetCadGeometryTool(server);
  registerTraceCadImportTool(server);
  registerPurgeCadLeftoversTool(server);
  return getTool;
}

describe("CAD tools", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: {} });
  });

  it("forwards link_cad with dialog defaults", async () => {
    await setup()("link_cad").invoke({ path: "D:\\jobs\\P181\\S-101.dwg", placementLevel: "L1" });
    expect(sendCommand).toHaveBeenCalledWith(
      "link_cad",
      {
        path: "D:\\jobs\\P181\\S-101.dwg",
        placementLevel: "L1",
        mode: "link",
        currentViewOnly: false,
        units: "auto",
        positioning: "origin",
        colorMode: "preserve",
        layerSelection: "all",
        correctLinesOffAxis: true,
        orientToView: false,
        pin: true,
      },
      180000
    );
  });

  it("accepts an import into one view with explicit options", () => {
    expect(() =>
      setup()("link_cad").parse({
        path: "\\\\server\\share\\detail.DXF",
        mode: "import",
        viewId: 1234,
        currentViewOnly: true,
        units: "inch",
        positioning: "center",
        colorMode: "blackAndWhite",
        layerSelection: "specified",
        layerNames: ["S-CONC", "S-REIN"],
        hideLayers: ["*TEXT*"],
        correctLinesOffAxis: false,
        pin: false,
      })
    ).not.toThrow();
  });

  it("rejects relative paths, other formats and bad enums", () => {
    const tool = setup()("link_cad");
    expect(() => tool.parse({ path: "plans/S-101.dwg" })).toThrow();
    expect(() => tool.parse({ path: "C:\\a\\b.pdf" })).toThrow();
    expect(() => tool.parse({ path: "C:\\a\\b.dwg", units: "yard" })).toThrow();
    expect(() => tool.parse({ path: "C:\\a\\b.dwg", positioning: "site" })).toThrow();
    expect(() => tool.parse({ path: "C:\\a\\b.dwg", customScale: 0 })).toThrow();
  });

  it("reports cross-field problems without calling Revit", async () => {
    const tool = setup()("link_cad");
    const specified = await tool.invoke({ path: "C:\\a\\b.dwg", layerSelection: "specified" });
    expect(specified.isError).toBe(true);
    const custom = await tool.invoke({ path: "C:\\a\\b.dwg", units: "custom" });
    expect(custom.isError).toBe(true);
    const point = await tool.invoke({ path: "C:\\a\\b.dwg", positioning: "shared", insertionPoint: { x: 0, y: 0 } });
    expect(point.isError).toBe(true);
    expect(sendCommand).not.toHaveBeenCalled();
  });

  it("forwards get_cad_geometry filters with defaults", async () => {
    await setup()("get_cad_geometry").invoke({
      importId: 501,
      layers: ["S-*"],
      region: { minX: 0, minY: 0, maxX: 54400, maxY: 28000 },
      kinds: ["lines", "polylines"],
    });
    expect(sendCommand).toHaveBeenCalledWith(
      "get_cad_geometry",
      {
        importId: 501,
        layers: ["S-*"],
        region: { minX: 0, minY: 0, maxX: 54400, maxY: 28000 },
        kinds: ["lines", "polylines"],
        summary: false,
        regionMode: "intersects",
        maxItems: 5000,
      },
      180000
    );
  });

  it("allows listing without importId and rejects bad regions/kinds", () => {
    const tool = setup()("get_cad_geometry");
    expect(tool.parse({})).toMatchObject({ summary: false, maxItems: 5000 });
    expect(() => tool.parse({ importId: 1, region: { minX: 10, minY: 0, maxX: 5, maxY: 10 } })).toThrow();
    expect(() => tool.parse({ importId: 1, kinds: ["texts"] })).toThrow();
    expect(() => tool.parse({ importId: 1, maxItems: 100001 })).toThrow();
  });

  it("defaults trace_cad_import to a detail-line dry run", () => {
    const parsed = setup()("trace_cad_import").parse({
      importId: 77,
      lineStyleMap: { "S-CONC": "PEN4", "*HIDDEN*": "Hidden" },
    });
    expect(parsed).toMatchObject({
      lineKind: "detail",
      dryRun: true,
      deleteImport: false,
      visibleLayersOnly: true,
      maxCurves: 20000,
      minLength: 0,
    });
    expect(() => setup()("trace_cad_import").parse({})).toThrow();
    expect(() => setup()("trace_cad_import").parse({ importId: 77, lineKind: "symbolic" })).toThrow();
  });

  it("defaults purge_cad_leftovers to a dry run and accepts a remap target", async () => {
    const tool = setup()("purge_cad_leftovers");
    await tool.invoke({ remapTo: "Concrete" });
    expect(sendCommand).toHaveBeenCalledWith("purge_cad_leftovers", { remapTo: "Concrete", dryRun: true }, 300000);
    expect(() => tool.parse({ include: ["materials"] })).toThrow();
    expect(() => tool.parse({ remapTo: 0 })).toThrow();
  });

  it("marks the cleanup tools destructive and the reader read-only", () => {
    expect(TOOL_MANIFEST.get_cad_geometry).toMatchObject({ readOnly: true });
    expect(TOOL_MANIFEST.link_cad).toMatchObject({ readOnly: false });
    expect(TOOL_MANIFEST.trace_cad_import).toMatchObject({ readOnly: false, destructive: true });
    expect(TOOL_MANIFEST.purge_cad_leftovers).toMatchObject({ readOnly: false, destructive: true });
  });
});
