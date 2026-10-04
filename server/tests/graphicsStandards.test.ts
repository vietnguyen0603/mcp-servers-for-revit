import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerManageGraphicsStandardsTool } from "../src/tools/manage_graphics_standards.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function tool() {
  const { server, getTool } = createFakeMcpServer();
  registerManageGraphicsStandardsTool(server);
  return getTool("manage_graphics_standards");
}

const hidden = {
  name: "S-HIDDEN",
  segments: [
    { kind: "dash", lengthMm: 3 },
    { kind: "space", lengthMm: 1.5 },
  ],
};

describe("manage_graphics_standards", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: { succeeded: 1, failed: 0 } });
  });

  it("is a modifying annotate/views tool", () => {
    const entry = TOOL_MANIFEST.manage_graphics_standards;
    expect(entry).toBeDefined();
    expect(entry).toMatchObject({ readOnly: false });
    expect(entry.catalogs).toEqual(expect.arrayContaining(["annotate", "views"]));
  });

  it("forwards all sections with defaults applied", async () => {
    const t = tool();
    await t.invoke({
      linePatterns: [hidden],
      lineStyles: [{ name: "S-HIDDEN", weight: 2, color: [0, 0, 0], pattern: "S-HIDDEN" }],
      fillPatterns: [
        { name: "Concrete 45", simple: { angleDeg: 45, spacingMm: 3, crosshatch: true } },
        {
          name: "PC-1150",
          target: "model",
          grids: [{ angleDeg: 0, offsetMm: 200, segments: [100, 50] }],
        },
      ],
      objectStyles: [{ category: "Structural Framing/Hidden Lines", projectionWeight: 2, pattern: "S-HIDDEN" }],
      list: true,
    });
    const [command, params] = sendCommand.mock.calls[0];
    expect(command).toBe("manage_graphics_standards");
    expect(params.linePatterns[0]).toEqual({ ...hidden, ifExists: "update" });
    expect(params.fillPatterns[0]).toMatchObject({ target: "drafting", ifExists: "update" });
    expect(params.fillPatterns[0].simple).toEqual({ angleDeg: 45, spacingMm: 3, crosshatch: true });
    expect(params.fillPatterns[1].grids[0]).toEqual({ angleDeg: 0, offsetMm: 200, shiftMm: 0, segments: [100, 50] });
    expect(params.list).toBe(true);
  });

  it("accepts dots without a length and rejects malformed line patterns", () => {
    const t = tool();
    expect(() =>
      t.parse({
        linePatterns: [
          {
            name: "Dash dot",
            segments: [
              { kind: "dash", lengthMm: 6 },
              { kind: "space", lengthMm: 1.5 },
              { kind: "dot" },
              { kind: "space", lengthMm: 1.5 },
            ],
          },
        ],
      })
    ).not.toThrow();
    // odd count
    expect(() => t.parse({ linePatterns: [{ name: "x", segments: [...hidden.segments, { kind: "dash", lengthMm: 1 }] }] })).toThrow(/even/);
    // starts with a space
    expect(() =>
      t.parse({ linePatterns: [{ name: "x", segments: [{ kind: "space", lengthMm: 1 }, { kind: "dash", lengthMm: 1 }] }] })
    ).toThrow(/alternate/);
    // dash without length
    expect(() =>
      t.parse({ linePatterns: [{ name: "x", segments: [{ kind: "dash" }, { kind: "space", lengthMm: 1 }] }] })
    ).toThrow(/lengthMm/);
    expect(() => t.parse({ linePatterns: [{ ...hidden, ifExists: "replace" }] })).toThrow();
  });

  it("requires exactly one fill pattern definition", () => {
    const t = tool();
    expect(() => t.parse({ fillPatterns: [{ name: "x" }] })).toThrow(/exactly one/);
    expect(() =>
      t.parse({ fillPatterns: [{ name: "x", solid: true, simple: { spacingMm: 2 } }] })
    ).toThrow(/exactly one/);
    expect(() => t.parse({ fillPatterns: [{ name: "Solid gray", solid: true }] })).not.toThrow();
    expect(() => t.parse({ fillPatterns: [{ name: "x", grids: [{ offsetMm: 0 }] }] })).toThrow();
    expect(() => t.parse({ fillPatterns: [{ name: "x", grids: [{ offsetMm: 2, segments: [1] }] }] })).toThrow(/even/);
    expect(() => t.parse({ fillPatterns: [{ name: "x", target: "paper", solid: true }] })).toThrow();
  });

  it("validates line styles and object styles", () => {
    const t = tool();
    expect(() => t.parse({ lineStyles: [{ name: "PEN3", weight: 17 }] })).toThrow();
    expect(() => t.parse({ lineStyles: [{ name: "PEN3", color: [0, 0, 256] }] })).toThrow();
    expect(() => t.parse({ lineStyles: [{ name: "  " }] })).toThrow();
    expect(() => t.parse({ objectStyles: [{ category: "Walls" }] })).toThrow(/Nothing to change/);
    expect(() => t.parse({ objectStyles: [{ category: "Walls", cutWeight: 5, material: "Concrete" }] })).not.toThrow();
    expect(() => t.parse({ objectStyles: [{ category: "Walls", bogus: 1 }] })).toThrow();
  });

  it("forwards lineWeights (answered with a not-supported note by Revit)", async () => {
    const t = tool();
    expect(t.description).toMatch(/lineWeights/);
    const lineWeights = { annotation: [{ pen: 1, widthMm: 0.13 }], model: [{ pen: 3, widthMm: 0.35, scale: "1:100" }] };
    await t.invoke({ lineWeights });
    expect(sendCommand).toHaveBeenCalledWith("manage_graphics_standards", { lineWeights });
    expect(() => t.parse({ lineWeights: { annotation: [{ pen: 17, widthMm: 1 }] } })).toThrow();
  });

  it("lists with options and rejects an empty call without contacting Revit", async () => {
    const t = tool();
    await t.invoke({ list: true, listOptions: { sections: ["fillPatterns"], nameContains: "Concrete" } });
    expect(sendCommand).toHaveBeenCalledWith("manage_graphics_standards", {
      list: true,
      listOptions: { sections: ["fillPatterns"], nameContains: "Concrete" },
    });
    expect(() => t.parse({ list: true, listOptions: { sections: ["walls"] } })).toThrow();

    sendCommand.mockClear();
    const empty = await t.invoke({});
    expect(empty.isError).toBe(true);
    const listFalse = await t.invoke({ list: false });
    expect(listFalse.isError).toBe(true);
    expect(sendCommand).not.toHaveBeenCalled();
  });

  it("flags item failures through the batch outcome", async () => {
    sendCommand.mockResolvedValue({
      Success: true,
      Message: "Graphics standards: 1 created, 0 updated, 0 skipped, 1 failed.",
      Response: { succeeded: 1, failed: 1, linePatterns: [] },
    });
    const result = await tool().invoke({ linePatterns: [hidden, { ...hidden, name: "B" }] });
    expect(result.content[0].text).toMatch(/WARNING: 1 of 2/);
  });
});
