import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { registerGetColumnWallRegisterDataTool } from "../src/tools/get_column_wall_register_data.js";
import {
  resetConnectionMock,
  sendCommand,
  withRevitConnection,
} from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

const TOOL = "get_column_wall_register_data";

function setup() {
  const { server, tools, getTool } = createFakeMcpServer();
  registerGetColumnWallRegisterDataTool(server);
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
      includeArchitecturalColumns: false,
      corePrefixes: [],
    });
  });

  it("forwards architectural-column and core-prefix options", async () => {
    const { tool } = setup();
    sendCommand.mockResolvedValue({ records: [] });

    await tool.invoke({
      includeArchitecturalColumns: true,
      corePrefixes: ["CW", "SW"],
    });

    const [, params] = sendCommand.mock.calls[0];
    expect(params).toMatchObject({
      includeArchitecturalColumns: true,
      corePrefixes: ["CW", "SW"],
    });
  });

  it("passes the Revit response through pretty-printed JSON", async () => {
    const { tool } = setup();
    const response = { schemaVersion: "1.0", records: [{ uniqueId: "c1" }] };
    sendCommand.mockResolvedValue(response);

    const result = await tool.invoke({});

    expect(result.isError).toBeUndefined();
    expect(result.content[0]).toEqual({
      type: "text",
      text: JSON.stringify(response, null, 2),
    });
  });

  it("returns an isError result when the Revit connection fails", async () => {
    const { tool } = setup();
    sendCommand.mockRejectedValueOnce(new Error("no active document"));

    const result = await tool.invoke({});

    expect(result.isError).toBe(true);
    expect(result.content[0].text).toContain(
      `${TOOL} failed: no active document`
    );
  });

  it("rejects invalid boolean and oversized core prefixes", () => {
    const { tool } = setup();

    expect(() => tool.parse({ includeArchitecturalColumns: "yes" })).toThrow();
    expect(() =>
      tool.parse({ corePrefixes: ["x".repeat(33)] })
    ).toThrow();
  });
});
