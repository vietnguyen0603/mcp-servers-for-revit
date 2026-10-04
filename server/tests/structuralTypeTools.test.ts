import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerCreateFamilyTypeTool } from "../src/tools/create_family_type.js";
import { registerLoadFamilyTool } from "../src/tools/load_family.js";
import { registerCreateSurfaceBasedElementTool } from "../src/tools/create_surface_based_element.js";
import { registerCreateLineBasedElementTool } from "../src/tools/create_line_based_element.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerCreateFamilyTypeTool(server);
  registerLoadFamilyTool(server);
  registerCreateSurfaceBasedElementTool(server);
  registerCreateLineBasedElementTool(server);
  return getTool;
}

describe("structural type tools", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: { succeeded: 1, failed: 0 } });
  });

  it("forwards create_family_type with mm parameters and thickness", async () => {
    const tool = setup()("create_family_type");
    const types = [
      { familyName: "M_WWF-Welded Wide Flange-Column", newName: "C2 I1000x1000x20x20", parameters: { d: 1000, bf: 1000, tw: 20, tf: 20 } },
      { category: "OST_Floors", newName: "Slab 150", thickness: 150 },
    ];
    await tool.invoke({ types });
    expect(sendCommand).toHaveBeenCalledWith("create_family_type", { types });
    expect(tool.description).toMatch(/millimetres/);
  });

  it("requires a source and a name for create_family_type", () => {
    const tool = setup()("create_family_type");
    expect(() => tool.parse({ types: [{ newName: "X" }] })).toThrow();
    expect(() => tool.parse({ types: [{ familyName: "F" }] })).toThrow();
    expect(() => tool.parse({ types: [{ familyName: "F", newName: "X", thickness: -1 }] })).toThrow();
  });

  it("forwards load_family by name, path and search", async () => {
    const tool = setup()("load_family");
    await tool.invoke({ families: [{ name: "M_Pile Cap-4 Pile" }], searchOnly: true });
    expect(sendCommand).toHaveBeenCalledWith("load_family", { families: [{ name: "M_Pile Cap-4 Pile" }], searchOnly: true });
    expect(() => tool.parse({ families: [{}] })).toThrow();
  });

  it("accepts structural on floors and walls", () => {
    const getTool = setup();
    const p = { x: 0, y: 0, z: 0 };
    const floor = {
      name: "slab", category: "OST_Floors", thickness: 150, baseLevel: 0, baseOffset: 0, structural: true,
      boundary: { outerLoop: [{ p0: p, p1: { x: 1, y: 0, z: 0 } }, { p0: { x: 1, y: 0, z: 0 }, p1: { x: 1, y: 1, z: 0 } }, { p0: { x: 1, y: 1, z: 0 }, p1: p }] },
    };
    expect(() => getTool("create_surface_based_element").parse({ data: [floor] })).not.toThrow();
    const wall = { category: "OST_Walls", locationLine: { p0: p, p1: { x: 1000, y: 0, z: 0 } }, thickness: 400, height: 5450, baseLevel: -5500, baseOffset: 0, structural: true };
    expect(() => getTool("create_line_based_element").parse({ data: [wall] })).not.toThrow();
    expect(getTool("create_surface_based_element").description).toMatch(/structural/);
  });

  it("registers both tools in the structure catalog", () => {
    expect(TOOL_MANIFEST.create_family_type.catalogs).toContain("structure");
    expect(TOOL_MANIFEST.load_family.catalogs).toContain("structure");
  });
});
