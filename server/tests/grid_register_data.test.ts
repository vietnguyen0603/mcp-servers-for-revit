import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { registerGetGridRegisterDataTool } from "../src/tools/get_grid_register_data.js";
import {
  resetConnectionMock,
  sendCommand,
  withRevitConnection,
} from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

const TOOL = "get_grid_register_data";

function setup() {
  const { server, tools, getTool } = createFakeMcpServer();
  registerGetGridRegisterDataTool(server);
  return { tools, tool: getTool(TOOL) };
}

describe(TOOL, () => {
  beforeEach(() => {
    resetConnectionMock();
  });

  it("registers exactly the intended tool with a per-instance mm description", () => {
    const { tools, tool } = setup();

    expect(tools.size).toBe(1);
    expect(tool.name).toBe(TOOL);

    const description = tool.description.toLowerCase();
    expect(description).toContain("one record per");
    expect(description).toContain("millimetres");
  });

  it("dispatches the exact case-sensitive method with resolved defaults", async () => {
    const { tool } = setup();
    sendCommand.mockResolvedValue({ records: [] });

    await tool.invoke({});

    expect(withRevitConnection).toHaveBeenCalledTimes(1);
    expect(sendCommand).toHaveBeenCalledTimes(1);
    expect(sendCommand).toHaveBeenCalledWith(TOOL, {
      levelIds: [],
      levelNames: [],
      pageSize: 100,
      designOptionPolicy: "primary",
      includeLinkedModels: false,
      coordinateSystem: "project",
      includeEvidence: true,
      tolerances: {
        angularDegrees: 0.5,
        intersectionMm: 5,
        groupingMm: 10,
        supportSearchMm: 500,
        snapMm: 1,
      },
      parameterMap: {},
      originGridUniqueId: null,
      originGridName: null,
      axisAssignments: [],
    });
  });

  it("forwards tool-specific options and omits unset optional identifiers", async () => {
    const { tool } = setup();
    sendCommand.mockResolvedValue({ records: [] });

    await tool.invoke({
      levelNames: ["Level 1"],
      viewId: "42",
      originGridName: "A",
      axisAssignments: [{ axisFamily: "x", gridNames: ["A", "B"] }],
    });

    const [, params] = sendCommand.mock.calls[0];
    expect(params).toMatchObject({
      levelNames: ["Level 1"],
      viewId: "42",
      originGridName: "A",
      originGridUniqueId: null,
      axisAssignments: [{ axisFamily: "x", gridNames: ["A", "B"] }],
    });
    expect("phaseId" in params).toBe(false);
    expect("cursor" in params).toBe(false);
  });

  it("passes the Revit response through pretty-printed JSON", async () => {
    const { tool } = setup();
    const response = { schemaVersion: "1.0", records: [{ uniqueId: "g1" }] };
    sendCommand.mockResolvedValue(response);

    const result = await tool.invoke({});

    expect(result.isError).toBeUndefined();
    expect(result.content).toHaveLength(1);
    expect(result.content[0]).toEqual({
      type: "text",
      text: JSON.stringify(response, null, 2),
    });
  });

  it("returns an isError result when the Revit connection fails", async () => {
    const { tool } = setup();
    sendCommand.mockRejectedValueOnce(new Error("Revit offline"));

    const result = await tool.invoke({});

    expect(result.isError).toBe(true);
    expect(result.content[0].text).toContain(
      `${TOOL} failed: Revit offline`
    );
  });

  it("rejects out-of-range pageSize during schema validation", () => {
    const { tool } = setup();
    expect(() => tool.parse({ pageSize: 0 })).toThrow();
    expect(() => tool.parse({ pageSize: 501 })).toThrow();
  });

  it("rejects duplicate axisAssignments axisFamily values", () => {
    const { tool } = setup();
    expect(() =>
      tool.parse({
        axisAssignments: [
          { axisFamily: "x", gridNames: ["A"] },
          { axisFamily: "x", gridNames: ["B"] },
        ],
      })
    ).toThrow(/axisFamily/);
  });

  it("rejects the active design-option policy without viewId", async () => {
    const { tool } = setup();

    const result = await tool.invoke({ designOptionPolicy: "active" });

    expect(result.isError).toBe(true);
    expect(result.content[0].text).toContain("requires viewId");
    expect(sendCommand).not.toHaveBeenCalled();
  });

  it("blocks a request that would exceed the 8 KB single-read limit", async () => {
    const { tool } = setup();
    const parameterMap: Record<string, string[]> = {};
    for (let keyIndex = 0; keyIndex < 32; keyIndex += 1) {
      const key = `k${keyIndex}`.padEnd(64, "a");
      parameterMap[key] = Array.from({ length: 20 }, (_, aliasIndex) =>
        `a${keyIndex}_${aliasIndex}`.padEnd(64, "b")
      );
    }

    const result = await tool.invoke({ parameterMap });

    expect(result.isError).toBe(true);
    expect(result.content[0].text).toContain("exceeds the 8192-byte");
    expect(sendCommand).not.toHaveBeenCalled();
  });
});
