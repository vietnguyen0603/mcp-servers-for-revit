import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerApplyViewTemplateTool } from "../src/tools/apply_view_template.js";
import { registerCreateViewFilterTool } from "../src/tools/create_view_filter.js";
import { registerOverrideGraphicsTool } from "../src/tools/override_graphics.js";
import { resetConnectionMock, sendCommand, withRevitConnection } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, tools, getTool } = createFakeMcpServer();
  registerApplyViewTemplateTool(server);
  registerCreateViewFilterTool(server);
  registerOverrideGraphicsTool(server);
  return { tools, getTool };
}

describe("view graphics tools", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ success: true, message: "ok", response: {} });
  });

  it("registers the three tools in the views catalog", () => {
    const { tools } = setup();
    expect([...tools.keys()].sort()).toEqual(["apply_view_template", "create_view_filter", "override_graphics"]);
    for (const name of tools.keys()) {
      expect(TOOL_MANIFEST[name].catalogs[0], name).toBe("views");
      expect(TOOL_MANIFEST[name].readOnly, name).toBe(false);
    }
  });

  it("apply_view_template requires a template unless removing", async () => {
    const { getTool } = setup();
    const tool = getTool("apply_view_template");

    const missing = await tool.invoke({ viewIds: [10] });
    expect(missing.isError).toBe(true);
    expect(withRevitConnection).not.toHaveBeenCalled();

    await tool.invoke({ viewIds: [10, 11], remove: true });
    expect(sendCommand).toHaveBeenLastCalledWith("apply_view_template", {
      viewIds: [10, 11],
      remove: true,
      applyPropertiesOnly: false,
    });

    await tool.invoke({ viewIds: [10], templateId: 99 });
    expect(sendCommand).toHaveBeenLastCalledWith("apply_view_template", {
      viewIds: [10],
      templateId: 99,
      remove: false,
      applyPropertiesOnly: false,
    });
    expect(() => tool.parse({ viewIds: [] })).toThrow();
  });

  it("validates override values", () => {
    const { getTool } = setup();
    const tool = getTool("override_graphics");

    expect(() => tool.parse({ categories: ["Walls"], overrides: { transparency: 101 } })).toThrow();
    expect(() => tool.parse({ categories: ["Walls"], overrides: { projectionLineWeight: 17 } })).toThrow();
    expect(() => tool.parse({ categories: ["Walls"], overrides: { projectionLineColor: [255, 0] } })).toThrow();
    expect(() => tool.parse({ categories: ["Walls"], overrides: { colour: [1, 2, 3] } })).toThrow();
    expect(
      tool.parse({
        elementIds: [5],
        overrides: { halftone: true, surfaceForegroundColor: [255, 0, 0], projectionLinePattern: "Dash" },
      })
    ).toMatchObject({ reset: false });
  });

  it("override_graphics needs targets and overrides (or reset) before contacting Revit", async () => {
    const { getTool } = setup();
    const tool = getTool("override_graphics");

    expect((await tool.invoke({ overrides: { halftone: true } })).isError).toBe(true);
    expect((await tool.invoke({ categories: ["Walls"] })).isError).toBe(true);
    expect(withRevitConnection).not.toHaveBeenCalled();

    await tool.invoke({ categories: ["Walls"], reset: true });
    expect(sendCommand).toHaveBeenLastCalledWith("override_graphics", { categories: ["Walls"], reset: true });
  });

  it("create_view_filter validates rules and applies defaults", async () => {
    const { getTool } = setup();
    const tool = getTool("create_view_filter");

    expect(() => tool.parse({ name: "F", categories: [] })).toThrow();
    expect(() =>
      tool.parse({ name: "F", categories: ["Walls"], rules: [{ parameter: "Mark", operator: "Equals" }] })
    ).toThrow(/requires a value/);
    expect(() =>
      tool.parse({ name: "F", categories: ["Walls"], rules: [{ parameter: "Mark", operator: "Like", value: "A" }] })
    ).toThrow();

    await tool.invoke({
      name: "Beams > 6m",
      categories: ["OST_StructuralFraming"],
      rules: [{ parameter: "Length", operator: "Greater", value: 6000 }, { parameter: "Mark", operator: "HasValue" }],
      viewIds: [100],
      overrides: { projectionLineColor: [255, 0, 0] },
    });
    expect(sendCommand).toHaveBeenLastCalledWith("create_view_filter", {
      name: "Beams > 6m",
      categories: ["OST_StructuralFraming"],
      rules: [
        { parameter: "Length", operator: "Greater", value: 6000 },
        { parameter: "Mark", operator: "HasValue" },
      ],
      logic: "And",
      viewIds: [100],
      overrides: { projectionLineColor: [255, 0, 0] },
      reuseExisting: false,
    });
  });
});
