import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerManageViewTemplatesTool } from "../src/tools/manage_view_templates.js";
import { resetConnectionMock, sendCommand, withRevitConnection } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerManageViewTemplatesTool(server);
  return getTool("manage_view_templates");
}

const framingPlan = [
  { action: "create", name: "S-FRAMING PLAN 1-150", viewType: "StructuralPlan", ifExists: "reuse" },
  {
    action: "modify",
    templateName: "S-FRAMING PLAN 1-150",
    scale: "1:150",
    detailLevel: "Medium",
    discipline: "Structural",
    showHiddenLines: "ByDiscipline",
    viewRange: { cutPlaneMm: 1200, bottomMm: -300, bottomLevelId: "Current" },
    categories: [
      {
        category: "Structural Columns",
        cutFill: { pattern: "<Solid fill>", color: [128, 128, 128] },
        cutLine: { weight: 5 },
      },
      { category: "Structural Framing/Hidden Lines", projectionLine: { pattern: "Hidden", weight: 1 } },
      { category: "Floors", projectionLine: { weight: 1 }, surfaceFill: { visible: false } },
      { category: "OST_Grids", visible: true },
    ],
    filters: [{ name: "Piles", visible: false }],
    controlled: { exclude: ["View Range"], include: ["V/G Overrides Model", "Filters"] },
  },
  { action: "apply", templateName: "S-FRAMING PLAN 1-150", viewIds: [101, 102] },
  { action: "list", nameContains: "S-" },
];

describe("manage_view_templates", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ success: true, message: "ok", response: { succeeded: 4, failed: 0, results: [] } });
  });

  it("is a modifying tool in the views catalog", () => {
    const entry = TOOL_MANIFEST["manage_view_templates"];
    expect(entry, "add manage_view_templates to toolManifest.ts").toBeDefined();
    expect(entry.catalogs[0]).toBe("views");
    expect(entry.readOnly).toBe(false);
  });

  it("forwards a full create/modify/apply/list sequence unchanged", async () => {
    const tool = setup();
    const result = await tool.invoke({ actions: framingPlan });
    expect(result.isError).toBeFalsy();
    expect(sendCommand).toHaveBeenCalledWith("manage_view_templates", { actions: framingPlan }, 300000);
  });

  it("rejects malformed values at the schema", () => {
    const tool = setup();
    const modify = (extra: Record<string, unknown>) => ({ actions: [{ action: "modify", templateId: 5, ...extra }] });

    expect(() => tool.parse({ actions: [] })).toThrow();
    expect(() => tool.parse({ actions: [{ action: "rename" }] })).toThrow();
    expect(() => tool.parse(modify({ scale: "150:1" }))).toThrow();
    expect(() => tool.parse(modify({ detailLevel: "High" }))).toThrow();
    expect(() => tool.parse(modify({ categories: [{ category: "Walls", cutLine: { weight: 17 } }] }))).toThrow();
    expect(() => tool.parse(modify({ categories: [{ category: "Walls", cutFill: { color: [1, 2] } }] }))).toThrow();
    expect(() => tool.parse(modify({ categories: [{ category: "Walls", colour: [1, 2, 3] }] }))).toThrow();
    expect(() => tool.parse(modify({ showHiddenLines: "Some" }))).toThrow();
    expect(() => tool.parse(modify({ viewRange: { cutPlaneMm: 1200, topLevelId: "Above" } }))).toThrow();
    expect(() =>
      tool.parse({ actions: [{ action: "create", name: "X", viewType: "Schedule" }] })
    ).toThrow();

    expect(tool.parse(modify({ scale: 100, filters: [{ name: "F", overrides: { halftone: true } }] }))).toBeTruthy();
  });

  it("checks cross-field rules before contacting Revit", async () => {
    const tool = setup();
    const cases: Array<[unknown[], RegExp]> = [
      [[{ action: "create", name: "T" }], /requires viewType, fromViewId or fromTemplate/],
      [[{ action: "create", name: "T", fromViewId: 1, fromTemplate: "A" }], /not both/],
      [[{ action: "modify", scale: 100 }], /templateId\/templateName OR viewIds/],
      [[{ action: "modify", templateId: 1, viewIds: [2], scale: 100 }], /templateId\/templateName OR viewIds/],
      [[{ action: "modify", templateId: 1 }], /nothing to change/],
      [[{ action: "modify", viewIds: [2], controlled: { include: ["View Range"] } }], /templates only/],
      [[{ action: "apply", viewIds: [2] }], /requires templateId or templateName/],
    ];
    for (const [actions, message] of cases) {
      const result = await tool.invoke({ actions });
      expect(result.isError, JSON.stringify(actions)).toBe(true);
      expect(result.content[0].text).toMatch(message);
    }
    expect(withRevitConnection).not.toHaveBeenCalled();
  });

  it("rejects Revit-prohibited characters in template names before contacting Revit", async () => {
    const tool = setup();
    const cases: Array<[unknown[], RegExp]> = [
      [[{ action: "create", name: "S-PLAN 1:150", viewType: "StructuralPlan" }], /name 'S-PLAN 1:150' contains ':'/],
      [[{ action: "create", name: "A<B>", viewType: "Section" }], /'<' '>'/],
      [[{ action: "create", name: "OK", fromTemplate: "Bad|Name" }], /fromTemplate .* contains '\|'/],
      [[{ action: "modify", templateName: "T[1]", scale: 100 }], /templateName .* contains '\[' '\]'/],
      [[{ action: "apply", templateName: "T?", viewIds: [1] }], /contains '\?'/],
    ];
    for (const [actions, message] of cases) {
      const result = await tool.invoke({ actions });
      expect(result.isError, JSON.stringify(actions)).toBe(true);
      expect(result.content[0].text).toMatch(message);
      expect(result.content[0].text).toMatch(/cannot contain/);
    }
    expect(withRevitConnection).not.toHaveBeenCalled();

    const ok = await tool.invoke({ actions: [{ action: "create", name: "S-FRAMING PLAN 1-150 (S)", viewType: "StructuralPlan" }] });
    expect(ok.isError).toBeFalsy();
  });

  it("documents templateKind and the Floors transparency warning", () => {
    const tool = setup();
    expect(tool.description).toMatch(/templateKind/);
    expect(tool.description).toMatch(/reset:true, transparency:0/);
  });

  it("flags partial failures from Revit", async () => {
    const tool = setup();
    sendCommand.mockResolvedValueOnce({
      success: true,
      message: "1 of 2 actions succeeded",
      response: { succeeded: 1, failed: 1, results: [] },
    });
    const result = await tool.invoke({ actions: [{ action: "list" }, { action: "apply", templateId: 9, viewIds: [1] }] });
    expect(result.content[0].text).toMatch(/^WARNING: 1 of 2/);
  });
});
