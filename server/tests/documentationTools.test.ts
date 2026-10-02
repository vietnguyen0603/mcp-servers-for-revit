import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { resolveCatalog } from "../src/catalog/catalogs.js";
import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerCreateScheduleTool } from "../src/tools/create_schedule.js";
import { registerCreateSheetTool } from "../src/tools/create_sheet.js";
import { registerCreateTextNoteTool } from "../src/tools/create_text_note.js";
import { registerCreateViewTool } from "../src/tools/create_view.js";
import { registerDuplicateViewTool } from "../src/tools/duplicate_view.js";
import { registerGetScheduleDataTool } from "../src/tools/get_schedule_data.js";
import { registerListSheetsTool } from "../src/tools/list_sheets.js";
import { registerListViewsTool } from "../src/tools/list_views.js";
import { registerPlaceViewportTool } from "../src/tools/place_viewport.js";
import { registerTagElementsTool } from "../src/tools/tag_elements.js";
import { resetConnectionMock, sendCommand, withRevitConnection } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

const VIEW_TOOLS = [
  "list_views",
  "list_sheets",
  "create_view",
  "duplicate_view",
  "create_sheet",
  "place_viewport",
  "create_schedule",
  "get_schedule_data",
];
const ANNOTATE_TOOLS = ["create_text_note", "tag_elements"];

function setup() {
  const { server, tools, getTool } = createFakeMcpServer();
  for (const register of [
    registerListViewsTool,
    registerListSheetsTool,
    registerCreateViewTool,
    registerDuplicateViewTool,
    registerCreateSheetTool,
    registerPlaceViewportTool,
    registerCreateScheduleTool,
    registerGetScheduleDataTool,
    registerCreateTextNoteTool,
    registerTagElementsTool,
  ]) {
    register(server);
  }
  return { tools, getTool };
}

describe("view, sheet and annotation tools", () => {
  beforeEach(() => {
    resetConnectionMock();
  });

  it("places view/sheet tools in the views catalog, separate from annotate", () => {
    for (const name of VIEW_TOOLS) expect(TOOL_MANIFEST[name].catalogs[0], name).toBe("views");
    for (const name of ANNOTATE_TOOLS) expect(TOOL_MANIFEST[name].catalogs, name).toEqual(["annotate"]);
    expect(TOOL_MANIFEST.list_views.readOnly).toBe(true);
    expect(TOOL_MANIFEST.get_schedule_data.readOnly).toBe(true);
    expect(TOOL_MANIFEST.create_sheet.readOnly).toBe(false);
    expect(resolveCatalog("Sheets")?.id).toBe("views");
    expect(resolveCatalog("schedule")?.id).toBe("views");
    expect(resolveCatalog("drafting")?.id).toBe("annotate");
  });

  it("dispatches each tool under its own case-sensitive command name", async () => {
    const { tools } = setup();
    expect([...tools.keys()].sort()).toEqual([...VIEW_TOOLS, ...ANNOTATE_TOOLS].sort());

    sendCommand.mockResolvedValue({ success: true, message: "ok", response: {} });
    await tools.get("list_views")!.invoke({});
    expect(sendCommand).toHaveBeenLastCalledWith("list_views", {
      includeTemplates: false,
      includeViewFamilyTypes: false,
      limit: 500,
    });
  });

  it("validates per-view-type requirements in create_view", () => {
    const { getTool } = setup();
    const tool = getTool("create_view");

    expect(() => tool.parse({ views: [{ viewType: "FloorPlan" }] })).toThrow(/levelId or levelName/);
    expect(() => tool.parse({ views: [{ viewType: "Section" }] })).toThrow(/sectionLine/);
    expect(() => tool.parse({ views: [{ viewType: "Elevation" }] })).toThrow(/origin/);
    expect(() => tool.parse({ views: [{ viewType: "Isometric" }] })).toThrow();
    expect(
      tool.parse({
        views: [
          { viewType: "StructuralPlan", levelName: "L1", scale: 100 },
          { viewType: "Section", sectionLine: { start: { x: 0, y: 0 }, end: { x: 6000, y: 0 } } },
          { viewType: "Elevation", origin: { x: 0, y: -5000 }, lookDirection: { x: 0, y: 1 } },
        ],
      })
    ).toBeTruthy();
  });

  it("rejects non-positive element ids and empty batches", () => {
    const { getTool } = setup();
    expect(() => getTool("place_viewport").parse({ viewports: [{ sheetId: 0, viewId: 5 }] })).toThrow();
    expect(() => getTool("create_sheet").parse({ sheets: [] })).toThrow();
    expect(() => getTool("create_schedule").parse({ category: "Walls", fields: [] })).toThrow();
  });

  it("accepts schedule fields as names or objects and caps filters at 8", () => {
    const { getTool } = setup();
    const tool = getTool("create_schedule");
    const parsed = tool.parse({
      category: "OST_StructuralFraming",
      fields: ["Type Mark", { name: "Length", heading: "L (mm)" }],
      filters: [{ field: "Length", operator: "GreaterThan", value: 3000 }],
    });
    expect(parsed.itemized).toBe(true);
    expect(() =>
      tool.parse({
        category: "Walls",
        fields: ["Mark"],
        filters: Array.from({ length: 9 }, () => ({ field: "Mark", operator: "HasValue" })),
      })
    ).toThrow();
  });

  it("refuses tag_elements without targets before contacting Revit", async () => {
    const { getTool } = setup();
    const result = await getTool("tag_elements").invoke({});
    expect(result.isError).toBe(true);
    expect(withRevitConnection).not.toHaveBeenCalled();
  });

  it("flags Revit-side failures and connection errors as tool errors", async () => {
    const { getTool } = setup();
    const tool = getTool("get_schedule_data");

    sendCommand.mockResolvedValueOnce({ success: false, message: "Schedule 'X' not found." });
    const failed = await tool.invoke({ scheduleName: "X" });
    expect(failed.isError).toBe(true);
    expect(failed.content[0].text).toContain("not found");

    sendCommand.mockResolvedValueOnce({ success: true, message: "ok", response: { schedules: [] } });
    expect((await tool.invoke({})).isError).toBeUndefined();

    sendCommand.mockRejectedValueOnce(new Error("connection refused"));
    const offline = await tool.invoke({});
    expect(offline.isError).toBe(true);
    expect(offline.content[0].text).toBe("get_schedule_data failed: connection refused");
  });
});
