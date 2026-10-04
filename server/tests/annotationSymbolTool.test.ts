import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerPlaceAnnotationSymbolTool } from "../src/tools/place_annotation_symbol.js";
import { resetConnectionMock, sendCommand, withRevitConnection } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, tools, getTool } = createFakeMcpServer();
  registerPlaceAnnotationSymbolTool(server);
  return { tools, tool: getTool("place_annotation_symbol") };
}

const p = (x: number, y: number) => ({ x, y });

describe("place_annotation_symbol", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ success: true, message: "ok", response: {} });
  });

  it("is registered in the annotate catalog as a write tool", () => {
    const { tools, tool } = setup();
    expect([...tools.keys()]).toEqual(["place_annotation_symbol"]);
    expect(TOOL_MANIFEST.place_annotation_symbol.catalogs).toEqual(["annotate"]);
    expect(TOOL_MANIFEST.place_annotation_symbol.readOnly).toBe(false);
    expect(tool.description).toMatch(/sub-detail title/i);
    expect(tool.description).toMatch(/weld/i);
  });

  it("requires symbols unless listing types", async () => {
    const { tool } = setup();
    const empty = await tool.invoke({});
    expect(empty.isError).toBe(true);
    expect(withRevitConnection).not.toHaveBeenCalled();

    await tool.invoke({ listTypes: true, allCategories: true });
    expect(sendCommand).toHaveBeenLastCalledWith("place_annotation_symbol", { listTypes: true, allCategories: true });
  });

  it("validates symbol identity, location and leaders", () => {
    const { tool } = setup();
    expect(() => tool.parse({ symbols: [{ location: p(0, 0) }] })).toThrow(/familyTypeId/);
    expect(() => tool.parse({ symbols: [{ familyTypeId: 5 }] })).toThrow();
    expect(() => tool.parse({ symbols: [{ familyTypeId: 5, location: p(0, 0), leaders: [{}] }] })).toThrow();
    expect(() =>
      tool.parse({ symbols: [{ familyTypeId: 5, location: p(0, 0), leaders: [{ elbow: p(1, 1) }] }] })
    ).toThrow();
    expect(() => tool.parse({ symbols: [{ familyTypeId: 0, location: p(0, 0) }] })).toThrow();
    expect(() =>
      tool.parse({ symbols: [{ familyTypeId: 5, location: p(0, 0), parameters: { Detail: { nested: 1 } } }] })
    ).toThrow();
    expect(tool.parse({ symbols: [{ typeName: "Subtitle", location: p(0, 0) }] })).toBeTruthy();
  });

  it("forwards sub-detail titles and weld symbols with parameters and leaders", async () => {
    const { tool } = setup();
    const args = {
      viewId: 42,
      symbols: [
        {
          familyName: "_TT-SY-Subtitle",
          typeName: "Subtitle",
          location: p(0, -500),
          parameters: { Detail: "A", "Text Above": "BASE PLATE", "Text Below": "SCALE: 1:10", Length: 1200 },
        },
        {
          familyName: "TT SF_Weld_Symbol : Both",
          location: { x: 300, y: 200, z: 0 },
          rotationDegrees: 90,
          parameters: { "Top Weld Size": "6", "Field Weld": true, "Weld All Around": "No" },
          leaders: [{ end: p(100, 50), elbow: p(250, 200) }],
        },
      ],
    };
    const result = await tool.invoke(args);
    expect(result.isError).toBeUndefined();
    expect(sendCommand).toHaveBeenCalledWith("place_annotation_symbol", args);
  });

  it("flags failed Revit results as errors", async () => {
    const { tool } = setup();
    sendCommand.mockResolvedValueOnce({ success: false, message: "View not found." });
    const result = await tool.invoke({ symbols: [{ familyTypeId: 9, location: p(0, 0) }] });
    expect(result.isError).toBe(true);
  });
});
