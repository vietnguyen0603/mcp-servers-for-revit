import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { mkdtemp, rm, writeFile } from "fs/promises";
import os from "os";
import path from "path";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import {
  projectParameterDefinitionSchema,
  registerCreateProjectParameterTool,
} from "../src/tools/create_project_parameter.js";
import {
  parseExpression,
  registerSetParametersTool,
  setParameterItemSchema,
  validateSetParametersArgs,
} from "../src/tools/set_parameters.js";
import { registerProbeTagTypesTool } from "../src/tools/probe_tag_types.js";
import { registerTagElementsTool } from "../src/tools/tag_elements.js";
import { resetConnectionMock, sendCommand, withRevitConnection } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerCreateProjectParameterTool(server);
  registerSetParametersTool(server);
  registerProbeTagTypesTool(server);
  registerTagElementsTool(server);
  return getTool;
}

function ok() {
  sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: {} });
}

let dir: string;

beforeAll(async () => {
  dir = await mkdtemp(path.join(os.tmpdir(), "set-params-"));
});

afterAll(async () => {
  await rm(dir, { recursive: true, force: true });
});

describe("create_project_parameter", () => {
  beforeEach(() => {
    resetConnectionMock();
    ok();
  });

  it("applies defaults and forwards a create request", async () => {
    const tool = setup()("create_project_parameter");
    const result = await tool.invoke({ parameters: [{ name: "Tag Text", categories: ["StructuralFraming", "OST_StructuralColumns"] }] });
    expect(result.isError).toBeUndefined();
    const [command, params, timeout] = sendCommand.mock.calls[0];
    expect(command).toBe("create_project_parameter");
    expect(timeout).toBe(120000);
    expect(params.action).toBe("create");
    expect(params.parameters[0]).toMatchObject({ name: "Tag Text", dataType: "text", binding: "instance" });
  });

  it("lists when no parameters are given", async () => {
    const tool = setup()("create_project_parameter");
    await tool.invoke({});
    expect(sendCommand.mock.calls[0][1]).toEqual({ action: "list" });
  });

  it("refuses create without parameters before contacting Revit", async () => {
    const result = await setup()("create_project_parameter").invoke({ action: "create" });
    expect(result.isError).toBe(true);
    expect(withRevitConnection).not.toHaveBeenCalled();
  });

  it("validates names, data types and varyByGroup", () => {
    const base = { name: "Beam Label", categories: ["StructuralFraming"] };
    expect(projectParameterDefinitionSchema.safeParse({ ...base, dataType: "length", binding: "type", group: "dimensions" }).success).toBe(true);
    expect(projectParameterDefinitionSchema.safeParse({ ...base, varyByGroup: true }).success).toBe(true);
    expect(projectParameterDefinitionSchema.safeParse({ ...base, binding: "type", varyByGroup: true }).success).toBe(false);
    expect(projectParameterDefinitionSchema.safeParse({ ...base, name: "Bad{Name}" }).success).toBe(false);
    expect(projectParameterDefinitionSchema.safeParse({ ...base, dataType: "url" }).success).toBe(false);
    expect(projectParameterDefinitionSchema.safeParse({ ...base, categories: [] }).success).toBe(false);
    expect(projectParameterDefinitionSchema.safeParse({ ...base, guid: "not-a-guid" }).success).toBe(false);
    expect(projectParameterDefinitionSchema.safeParse({ ...base, extra: 1 }).success).toBe(false);
  });
});

describe("set_parameters expressions", () => {
  it("parses placeholders with formats and literal braces", () => {
    expect(parseExpression("{Mark}-{b}x{h:mm0}")).toEqual([
      { name: "Mark" },
      { name: "b" },
      { name: "h", format: "mm0" },
    ]);
    expect(parseExpression("{Reference Level:id} {{x}}")).toEqual([{ name: "Reference Level", format: "id" }]);
    expect(parseExpression("plain text")).toEqual([]);
    expect(parseExpression("{Type Name:UPPER}")).toEqual([{ name: "Type Name", format: "upper" }]);
  });

  it("rejects malformed expressions", () => {
    expect(() => parseExpression("{Mark")).toThrow(/Unclosed/);
    expect(() => parseExpression("Mark}")).toThrow(/Unmatched/);
    expect(() => parseExpression("{}")).toThrow(/Empty/);
    expect(() => parseExpression("{b:inch}")).toThrow(/Unknown format/);
    expect(() => parseExpression("{a{b}}")).toThrow(/Nested/);
  });

  it("validates the request mode", () => {
    expect(validateSetParametersArgs({ categories: ["StructuralFraming"], parameter: "Comments", value: "x" })).toBeNull();
    expect(validateSetParametersArgs({ categories: ["StructuralFraming"], parameter: "Comments" })).toMatch(/exactly one/);
    expect(validateSetParametersArgs({ categories: ["StructuralFraming"], value: "x" })).toMatch(/parameter/);
    expect(validateSetParametersArgs({ parameter: "Comments", value: "x" })).toMatch(/categories/);
    expect(
      validateSetParametersArgs({ items: [{ elementId: 1, parameter: "Mark", value: "B1" }], categories: ["Walls"] })
    ).toMatch(/not both/);
    expect(validateSetParametersArgs({ items: [{ elementId: 1, parameter: "Mark", value: "B1" }] })).toBeNull();
  });

  it("validates items", () => {
    expect(setParameterItemSchema.safeParse({ elementId: 5, parameter: "Mark", value: "C4" }).success).toBe(true);
    expect(setParameterItemSchema.safeParse({ elementId: 5, parameter: "Tag Text", expression: "{Mark}-{b}x{h}" }).success).toBe(true);
    expect(setParameterItemSchema.safeParse({ elementId: 5, parameter: "Mark" }).success).toBe(false);
    expect(setParameterItemSchema.safeParse({ elementId: 5, parameter: "Mark", value: "a", expression: "{b}" }).success).toBe(false);
    expect(setParameterItemSchema.safeParse({ elementId: 5, parameter: "Mark", expression: "{b" }).success).toBe(false);
  });
});

describe("set_parameters", () => {
  beforeEach(() => {
    resetConnectionMock();
  });

  it("sends filter mode with defaults and without bulk fields", async () => {
    ok();
    const tool = setup()("set_parameters");
    const result = await tool.invoke({
      categories: ["StructuralFraming"],
      levels: ["L5"],
      parameter: "Tag Text",
      expression: "{Mark}-{b}x{h}",
    });
    expect(result.isError).toBeUndefined();
    const [command, params, timeout] = sendCommand.mock.calls[0];
    expect(command).toBe("set_parameters");
    expect(timeout).toBe(600000);
    expect(params).toMatchObject({
      categories: ["StructuralFraming"],
      levels: ["L5"],
      parameter: "Tag Text",
      expression: "{Mark}-{b}x{h}",
      skipIfUnresolved: true,
      dryRun: false,
      maxElements: 20000,
    });
    expect(params).not.toHaveProperty("items");
  });

  it("refuses mixed modes before contacting Revit", async () => {
    const result = await setup()("set_parameters").invoke({
      categories: ["Walls"],
      parameter: "Mark",
      value: "W1",
      items: [{ elementId: 1, parameter: "Mark", value: "B1" }],
    });
    expect(result.isError).toBe(true);
    expect(withRevitConnection).not.toHaveBeenCalled();
  });

  it("sends explicit items in chunks with the shared options", async () => {
    sendCommand.mockImplementation(async (_command: string, params: { items: unknown[] }) => ({
      Success: true,
      Message: "ok",
      Response: {
        succeeded: params.items.length,
        failed: 0,
        results: params.items.map((_, index) => ({ index, success: true })),
      },
    }));
    const file = path.join(dir, "values.csv");
    await writeFile(file, "elementId,parameter,value\n11,Mark,007\n12,Comments,sup\n");
    const result = await setup()("set_parameters").invoke({
      items: [{ elementId: 10, parameter: "Tag Text", expression: "{Mark}-{b}x{h}" }],
      dataFile: file,
      onType: false,
    });
    expect(result.isError).toBeUndefined();
    const [command, params] = sendCommand.mock.calls[0];
    expect(command).toBe("set_parameters");
    expect(params.onType).toBe(false);
    expect(params.skipIfUnresolved).toBe(true);
    expect(params.items).toHaveLength(3);
    expect(params.items[2]).toEqual({ elementId: 12, parameter: "Comments", value: "sup" });
    expect(typeof params.items[1].value).toBe("string");
  });
});

describe("probe_tag_types", () => {
  beforeEach(() => {
    resetConnectionMock();
    ok();
  });

  it("forwards with defaults", async () => {
    await setup()("probe_tag_types").invoke({ category: "OST_StructuralFraming" });
    const [command, params] = sendCommand.mock.calls[0];
    expect(command).toBe("probe_tag_types");
    expect(params).toMatchObject({ category: "OST_StructuralFraming", includeMultiCategory: true, probeLengths: true, maxTagTypes: 60 });
  });

  it("needs a category or a sample element", async () => {
    const result = await setup()("probe_tag_types").invoke({});
    expect(result.isError).toBe(true);
    expect(withRevitConnection).not.toHaveBeenCalled();
  });
});

describe("tag_elements placement options", () => {
  beforeEach(() => {
    resetConnectionMock();
    ok();
  });

  it("keeps the old defaults", async () => {
    await setup()("tag_elements").invoke({ categories: ["OST_StructuralFraming"] });
    const params = sendCommand.mock.calls[0][1];
    expect(params).toMatchObject({ orientation: "Horizontal", avoidOverlaps: false, untaggedOnly: true, addLeader: false });
    expect(params).not.toHaveProperty("offsetAlongNormalMm");
  });

  it("accepts model orientation, normal offset and overlap avoidance", async () => {
    await setup()("tag_elements").invoke({
      categories: ["OST_StructuralFraming"],
      orientation: "Model",
      offsetAlongNormalMm: 400,
      avoidOverlaps: true,
      maxShiftTries: 12,
    });
    expect(sendCommand.mock.calls[0][1]).toMatchObject({
      orientation: "Model",
      offsetAlongNormalMm: 400,
      avoidOverlaps: true,
      maxShiftTries: 12,
    });
  });

  it("rejects unknown orientations and bad shift settings", () => {
    const tool = setup()("tag_elements");
    expect(() => tool.parse({ categories: ["Walls"], orientation: "Diagonal" })).toThrow();
    expect(() => tool.parse({ categories: ["Walls"], maxShiftTries: 0 })).toThrow();
    expect(() => tool.parse({ categories: ["Walls"], shiftStepMm: -5 })).toThrow();
  });
});
