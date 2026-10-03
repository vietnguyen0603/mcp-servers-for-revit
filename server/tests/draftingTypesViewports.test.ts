import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerCreateTextNoteTool } from "../src/tools/create_text_note.js";
import { registerCreateDimensionsTool } from "../src/tools/create_dimensions.js";
import { registerModifyAnnotationsTool } from "../src/tools/modify_annotations.js";
import { registerListDraftingTypesTool } from "../src/tools/list_drafting_types.js";
import { registerUpdateViewportsTool } from "../src/tools/update_viewports.js";
import { registerPlaceViewportTool } from "../src/tools/place_viewport.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerCreateTextNoteTool(server);
  registerCreateDimensionsTool(server);
  registerModifyAnnotationsTool(server);
  registerListDraftingTypesTool(server);
  registerUpdateViewportsTool(server);
  registerPlaceViewportTool(server);
  return getTool;
}

describe("drafting types and viewport tools", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: { succeeded: 1, failed: 0 } });
  });

  it("forwards textNoteTypeName on create_text_note", async () => {
    const tool = setup()("create_text_note");
    const note = { text: "SEE PLAN", location: { x: 0, y: 0 }, textNoteTypeName: '1/8" Arial -Arrow' };
    await tool.invoke({ notes: [note] });
    expect(sendCommand).toHaveBeenCalledWith("create_text_note", { notes: [note] });
    expect(tool.description).toMatch(/textNoteTypeName/);
    expect(() => tool.parse({ notes: [{ ...note, textNoteTypeName: "" }] })).toThrow();
  });

  it("accepts dimension text position and leader in create_dimensions and setDimensionText", async () => {
    const getTool = setup();
    const create = getTool("create_dimensions");
    const dimension = {
      startPoint: { x: 0, y: 0 },
      endPoint: { x: 100, y: 0 },
      text: { override: "EQ", leader: true },
      segments: [{ index: 0, position: { x: 50, y: 40 } }],
    };
    await create.invoke({ dimensions: [dimension] });
    expect(sendCommand).toHaveBeenCalledWith("create_dimensions", { dimensions: [dimension] });
    expect(() =>
      create.parse({ dimensions: [{ ...dimension, text: { position: { x: "a", y: 0 } } }] })
    ).toThrow();
    expect(() => create.parse({ dimensions: [{ ...dimension, text: { leader: "yes" } }] })).toThrow();

    const modify = getTool("modify_annotations");
    const op = { action: "setDimensionText", elementIds: [7], text: { position: { x: 1, y: 2, z: 0 }, leader: false } };
    await modify.invoke({ operations: [op] });
    expect(sendCommand).toHaveBeenLastCalledWith(
      "modify_annotations",
      expect.objectContaining({ operations: [expect.objectContaining(op)] })
    );
  });

  it("list_drafting_types is read-only in annotate and views and forwards filters", async () => {
    const tool = setup()("list_drafting_types");
    expect(TOOL_MANIFEST.list_drafting_types).toMatchObject({ catalogs: ["annotate", "views"], readOnly: true });
    await tool.invoke({});
    expect(sendCommand).toHaveBeenCalledWith("list_drafting_types", {});
    await tool.invoke({ kinds: ["textNoteTypes", "dimensionTypes"], nameContains: "Tick" });
    expect(sendCommand).toHaveBeenLastCalledWith("list_drafting_types", {
      kinds: ["textNoteTypes", "dimensionTypes"],
      nameContains: "Tick",
    });
    expect(() => tool.parse({ kinds: ["walls"] })).toThrow();
    expect(() => tool.parse({ kinds: [] })).toThrow();
  });

  it("update_viewports forwards numbers, anchors and label settings", async () => {
    const tool = setup()("update_viewports");
    expect(TOOL_MANIFEST.update_viewports).toMatchObject({ catalogs: ["views"], readOnly: false });
    expect(tool.description).toMatch(/collision/);
    const viewports = [
      { viewportId: 10, detailNumber: "2" },
      { viewportId: 11, detailNumber: " 1 ", viewportTypeName: "TT-Not To Scale" },
      {
        viewportId: 12,
        anchor: { viewPoint: { x: 0, y: 0 }, sheetPoint: { x: 120, y: 300 } },
        labelOffset: { x: 0, y: -10 },
        labelLineLength: 80,
      },
    ];
    await tool.invoke({ viewports });
    expect(sendCommand).toHaveBeenCalledWith("update_viewports", {
      viewports: [viewports[0], { ...viewports[1], detailNumber: "1" }, viewports[2]],
    });
  });

  it("update_viewports rejects empty or malformed items", () => {
    const tool = setup()("update_viewports");
    expect(() => tool.parse({ viewports: [] })).toThrow();
    expect(() => tool.parse({ viewports: [{ viewportId: 10 }] })).toThrow(/Nothing to change/);
    expect(() => tool.parse({ viewports: [{ viewportId: 10, detailNumber: "  " }] })).toThrow();
    expect(() => tool.parse({ viewports: [{ viewportId: 10, anchor: { viewPoint: { x: 0, y: 0 } } }] })).toThrow();
    expect(() => tool.parse({ viewports: [{ viewportId: 10, labelLineLength: -1 }] })).toThrow();
    expect(() => tool.parse({ viewports: [{ viewportId: 10, center: { x: 0, y: 0 }, bogus: 1 }] })).toThrow();
  });

  it("place_viewport forwards anchor and detailNumber", async () => {
    const tool = setup()("place_viewport");
    const item = {
      sheetId: 1,
      viewId: 2,
      anchor: { viewPoint: { x: 10, y: 20 }, sheetPoint: { x: 400, y: 250 } },
      detailNumber: "5",
    };
    await tool.invoke({ viewports: [item] });
    expect(sendCommand).toHaveBeenCalledWith("place_viewport", { viewports: [item] });
    expect(tool.description).toMatch(/update_viewports/);
  });
});
