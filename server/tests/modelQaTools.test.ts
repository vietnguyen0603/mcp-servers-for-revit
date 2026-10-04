import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { registerJoinElementsTool } from "../src/tools/join_elements.js";
import { registerCheckModelTool } from "../src/tools/check_model.js";
import { MODEL_QA_TIMEOUT_MS, normalizeCategory } from "../src/utils/modelQaSchemas.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, tools, getTool } = createFakeMcpServer();
  registerJoinElementsTool(server);
  registerCheckModelTool(server);
  return { tools, getTool };
}

describe("normalizeCategory", () => {
  it("maps friendly names and OST_ names", () => {
    expect(normalizeCategory("Walls")).toBe("OST_Walls");
    expect(normalizeCategory("Structural Columns")).toBe("OST_StructuralColumns");
    expect(normalizeCategory("structural_framing")).toBe("OST_StructuralFraming");
    expect(normalizeCategory("beams")).toBe("OST_StructuralFraming");
    expect(normalizeCategory("Slabs")).toBe("OST_Floors");
    expect(normalizeCategory("Foundations")).toBe("OST_StructuralFoundation");
    expect(normalizeCategory("OST_StructuralFoundation")).toBe("OST_StructuralFoundation");
    expect(normalizeCategory("ost_Walls")).toBe("OST_Walls");
    expect(normalizeCategory("Doors")).toBeUndefined();
  });
});

describe("join_elements", () => {
  beforeEach(() => resetConnectionMock());

  it("registers both model QA tools", () => {
    const { tools } = setup();
    expect([...tools.keys()].sort()).toEqual(["check_model", "join_elements"]);
  });

  it("normalises pairs, applies defaults and sends with a long timeout", async () => {
    const { getTool } = setup();
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: { totals: {} } });

    await getTool("join_elements").invoke({
      pairs: [
        { cut: "StructuralFraming", by: "Structural Columns" },
        { cut: "OST_Floors", by: "columns" },
      ],
      levels: ["L1"],
    });

    expect(sendCommand).toHaveBeenCalledWith(
      "join_elements",
      {
        pairs: [
          { cut: "OST_StructuralFraming", by: "OST_StructuralColumns" },
          { cut: "OST_Floors", by: "OST_StructuralColumns" },
        ],
        levels: ["L1"],
        mode: "join",
        batchSize: 500,
        maxErrors: 20,
      },
      MODEL_QA_TIMEOUT_MS
    );
  });

  it("rejects bad input", () => {
    const tool = setup().getTool("join_elements");
    expect(() => tool.parse({})).toThrow();
    expect(() => tool.parse({ pairs: [] })).toThrow();
    expect(() => tool.parse({ pairs: [{ cut: "Doors", by: "Walls" }] })).toThrow(/Unknown category/);
    expect(() => tool.parse({ pairs: [{ cut: "Walls" }] })).toThrow();
    expect(() => tool.parse({ pairs: [{ cut: "Walls", by: "Floors", extra: 1 }] })).toThrow();
    expect(() => tool.parse({ pairs: [{ cut: "Walls", by: "Floors" }], mode: "merge" })).toThrow();
    expect(() => tool.parse({ pairs: [{ cut: "Walls", by: "Floors" }], batchSize: 10 })).toThrow();
    expect(() => tool.parse({ pairs: [{ cut: "Walls", by: "Floors" }], elementIds: [0] })).toThrow();
    expect(tool.parse({ pairs: [{ cut: "Walls", by: "Floors" }], mode: "unjoin", elementIds: [12, 13] })).toMatchObject({
      mode: "unjoin",
      elementIds: [12, 13],
    });
  });

  it("flags a Revit failure as an error", async () => {
    const tool = setup().getTool("join_elements");
    sendCommand.mockResolvedValue({ Success: false, Message: "No active Revit document." });
    const result = await tool.invoke({ pairs: [{ cut: "Walls", by: "StructuralColumns" }] });
    expect(result.isError).toBe(true);
  });
});

describe("check_model", () => {
  beforeEach(() => resetConnectionMock());

  it("defaults to all checks", async () => {
    const tool = setup().getTool("check_model");
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: {} });
    await tool.invoke({});
    expect(sendCommand).toHaveBeenCalledWith(
      "check_model",
      {
        checks: ["counts", "elevations", "overlaps", "unsupported", "levelsWithoutFloor"],
        byType: false,
        sameCategoryOverlaps: true,
        minOverlapVolumeM3: 0.01,
        skipJoined: true,
        supportToleranceMm: 300,
        maxItems: 200,
      },
      MODEL_QA_TIMEOUT_MS
    );
  });

  it("normalises categories and clash pairs", () => {
    const tool = setup().getTool("check_model");
    const parsed = tool.parse({
      checks: ["overlaps"],
      categories: ["Walls", "Beams"],
      clashPairs: [{ a: "StructuralFraming", b: "Walls" }],
      outFile: "C:\\Temp\\check.json",
    });
    expect(parsed).toMatchObject({
      checks: ["overlaps"],
      categories: ["OST_Walls", "OST_StructuralFraming"],
      clashPairs: [{ a: "OST_StructuralFraming", b: "OST_Walls" }],
      outFile: "C:\\Temp\\check.json",
    });
  });

  it("rejects bad input", () => {
    const tool = setup().getTool("check_model");
    expect(() => tool.parse({ checks: [] })).toThrow();
    expect(() => tool.parse({ checks: ["clashes"] })).toThrow();
    expect(() => tool.parse({ categories: ["Furniture"] })).toThrow(/Unknown category/);
    expect(() => tool.parse({ outFile: "report.json" })).toThrow(/absolute/);
    expect(() => tool.parse({ outFile: "C:\\Temp\\report.txt" })).toThrow(/\.json/);
    expect(() => tool.parse({ maxItems: 0 })).toThrow();
    expect(() => tool.parse({ supportToleranceMm: -1 })).toThrow();
    expect(() => tool.parse({ clashPairs: [{ a: "Walls" }] })).toThrow();
  });
});
