import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerCreateMemberArrayTool } from "../src/tools/create_member_array.js";
import { registerCreateTrussesTool } from "../src/tools/create_trusses.js";
import {
  arrayMarks,
  clipToPolygon,
  expandPlanArray,
  generateMembers,
  layoutPositions,
  resolvePlanArray,
} from "../src/utils/memberArrays.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerCreateTrussesTool(server);
  registerCreateMemberArrayTool(server);
  return getTool;
}

const okResponse = async (_command: string, params: Record<string, unknown[]>) => {
  const items = (params.trusses ?? params.beams) as unknown[];
  return {
    Success: true,
    Response: {
      succeeded: items.length,
      failed: 0,
      results: items.map((_, index) => ({ index, success: true, id: 900 + index })),
    },
  };
};

describe("array geometry", () => {
  it("resolves count / spacing / distance", () => {
    expect(resolvePlanArray({ count: 16, spacing: 6000 })).toEqual({ count: 16, spacing: 6000 });
    expect(resolvePlanArray({ distance: 90000, spacing: 6000 })).toEqual({ count: 16, spacing: 6000 });
    expect(resolvePlanArray({ distance: 90000, count: 16 })).toEqual({ count: 16, spacing: 6000 });
    expect(() => resolvePlanArray({ count: 3, spacing: 1, distance: 2 })).toThrow(/not all three/);
    expect(() => resolvePlanArray({ spacing: 1 })).toThrow();
  });

  it("counts marks", () => {
    expect(arrayMarks("T1", 3)).toEqual(["T1", "T2", "T3"]);
    expect(arrayMarks("T09", 2)).toEqual(["T09", "T10"]);
    expect(arrayMarks("PL", 2)).toEqual(["PL-1", "PL-2"]);
    expect(arrayMarks(5, 2)).toEqual([5, 6]);
    expect(arrayMarks(undefined, 2)).toEqual([undefined, undefined]);
  });

  it("copies a truss line to the left, right or along a vector", () => {
    const base = { start: { x: 0, y: 0 }, end: { x: 30000, y: 0 }, mark: "T1" };
    const left = expandPlanArray({ ...base, array: { count: 3, spacing: 6000 } });
    expect(left.map((t) => t.start)).toEqual([{ x: 0, y: 0 }, { x: 0, y: 6000 }, { x: 0, y: 12000 }]);
    expect(left[2]).toEqual({ start: { x: 0, y: 12000 }, end: { x: 30000, y: 12000 }, mark: "T3" });
    expect(left[0]).not.toHaveProperty("array");
    const right = expandPlanArray({ ...base, array: { count: 2, spacing: 6000, direction: "right" } });
    expect(right[1].end).toEqual({ x: 30000, y: -6000 });
    const vector = expandPlanArray({ ...base, array: { count: 2, spacing: 5000, direction: { x: 3, y: 4 } } });
    expect(vector[1].start).toEqual({ x: 3000, y: 4000 });
    expect(expandPlanArray(base)).toEqual([base]);
  });

  it("lays out positions by spacing, count and maxSpacing", () => {
    const line = { baseLine: { start: { x: 0, y: 0 }, end: { x: 1, y: 0 } } };
    expect(layoutPositions(10000, { ...line, spacing: 3000 })).toEqual([0, 3000, 6000, 9000]);
    expect(layoutPositions(10000, { ...line, spacing: 3000, includeEnd: true })).toEqual([0, 3000, 6000, 9000, 10000]);
    expect(layoutPositions(10000, { ...line, count: 5 })).toEqual([0, 2500, 5000, 7500, 10000]);
    expect(layoutPositions(10000, { ...line, maxSpacing: 1500 })).toHaveLength(8);
    expect(layoutPositions(10000, { ...line, count: 3, firstOffset: 100, lastOffset: 100 })).toEqual([100, 5000, 9900]);
    expect(() => layoutPositions(100, { ...line, count: 2, firstOffset: 80, lastOffset: 80 })).toThrow(/exceed/);
  });

  it("spaces purlins along a sloped roof from eave to ridge", () => {
    // 3:4 roof - eave at z 0 along x, ridge 4000 mm in plan and 3000 mm up: 5000 mm along the slope.
    const members = generateMembers({
      baseLine: { start: { x: 0, y: 0, z: 0 }, end: { x: 12000, y: 0, z: 0 } },
      farLine: { start: { x: 0, y: 4000, z: 3000 }, end: { x: 12000, y: 4000, z: 3000 } },
      spacing: 1250,
    });
    expect(members).toHaveLength(5);
    expect(members[1]).toMatchObject({
      start: { x: 0, y: 1000 },
      end: { x: 12000, y: 1000 },
      startOffset: 750,
      endOffset: 750,
      position: 1250,
    });
    expect(members[4].startOffset).toBe(3000);
    // The roof rises to the left of the eave direction: positive rotation of atan(3/4).
    expect(members[0].slopeRotationDeg).toBeCloseTo(36.87, 2);

    const otherWay = generateMembers({
      baseLine: { start: { x: 12000, y: 0, z: 0 }, end: { x: 0, y: 0, z: 0 } },
      farLine: { start: { x: 12000, y: 4000, z: 3000 }, end: { x: 0, y: 4000, z: 3000 } },
      count: 2,
    });
    expect(otherWay[0].slopeRotationDeg).toBeCloseTo(-36.87, 2);
  });

  it("offsets along a direction and trims to a boundary", () => {
    const members = generateMembers(
      {
        baseLine: { start: { x: -1000, y: 0 }, end: { x: 11000, y: 0 } },
        direction: { x: 0, y: 1 },
        distance: 6000,
        spacing: 3000,
      },
      [
        { x: 0, y: 0 },
        { x: 10000, y: 0 },
        { x: 10000, y: 6000 },
        { x: 0, y: 6000 },
      ]
    );
    expect(members.map((m) => [m.start.x, m.end.x])).toEqual([
      [0, 10000],
      [0, 10000],
      [0, 10000],
    ]);
    expect(members[0].slopeRotationDeg).toBe(0);
  });

  it("splits members crossing a concave boundary", () => {
    // U shape open at the top between x 4000 and 6000.
    const u = [
      { x: 0, y: 0 },
      { x: 10000, y: 0 },
      { x: 10000, y: 5000 },
      { x: 6000, y: 5000 },
      { x: 6000, y: 2000 },
      { x: 4000, y: 2000 },
      { x: 4000, y: 5000 },
      { x: 0, y: 5000 },
    ];
    expect(clipToPolygon({ x: -1, y: 3000 }, { x: 10001, y: 3000 }, u)).toHaveLength(2);
    expect(clipToPolygon({ x: -1, y: 1000 }, { x: 10001, y: 1000 }, u)).toHaveLength(1);
    expect(clipToPolygon({ x: -1, y: 9000 }, { x: 10001, y: 9000 }, u)).toHaveLength(0);
  });
});

describe("create_trusses", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockImplementation(okResponse);
  });

  it("expands an array of 16 trusses and tags results with source and copy", async () => {
    const tool = setup()("create_trusses");
    const truss = {
      familyName: "Truss_12Panel",
      typeName: "30m",
      start: { x: 0, y: 0 },
      end: { x: 30000, y: 0 },
      level: "Roof",
      startOffset: -200,
      bearingChord: "bottom" as const,
      height: 2500,
      memberTypes: { topChord: { typeId: 11 }, diagonalWeb: { familyName: "L-Angle", typeName: "L75x75x6" } },
      mark: "T1",
      array: { count: 16, spacing: 6000 },
    };
    const result = await tool.invoke({ trusses: [truss] });
    expect(sendCommand).toHaveBeenCalledTimes(1);
    const [command, params] = sendCommand.mock.calls[0] as [string, { trusses: Array<Record<string, unknown>> }];
    expect(command).toBe("create_trusses");
    expect(params.trusses).toHaveLength(16);
    expect(params.trusses[15]).toMatchObject({ start: { x: 0, y: 90000 }, end: { x: 30000, y: 90000 }, mark: "T16", height: 2500 });
    expect(params.trusses[0]).not.toHaveProperty("array");
    const parsed = JSON.parse(result.content[0].text);
    expect(parsed.Response.succeeded).toBe(16);
    expect(parsed.Response.results[15]).toMatchObject({ index: 15, source: 0, copy: 15, id: 915 });
  });

  it("validates items", () => {
    const tool = setup()("create_trusses");
    const base = { typeId: 1, start: { x: 0, y: 0 }, end: { x: 1, y: 0 }, level: "L1" };
    expect(() => tool.parse({ trusses: [base] })).not.toThrow();
    expect(() => tool.parse({ trusses: [{ ...base, typeId: undefined }] })).toThrow();
    expect(() => tool.parse({ trusses: [{ ...base, bearingChord: "middle" }] })).toThrow();
    expect(() => tool.parse({ trusses: [{ ...base, memberTypes: { web: { typeId: 2 } } }] })).toThrow();
    expect(() => tool.parse({ trusses: [{ ...base, memberTypes: { topChord: {} } }] })).toThrow();
    expect(() => tool.parse({ trusses: [{ ...base, height: 0 }] })).toThrow();
    expect(() => tool.parse({ trusses: [{ ...base, array: { count: 2, spacing: 10, foo: 1 } }] })).toThrow();
  });

  it("reports a bad array before sending", async () => {
    const result = await setup()("create_trusses").invoke({
      trusses: [{ typeId: 1, start: { x: 0, y: 0 }, end: { x: 1, y: 0 }, level: "L1", array: { spacing: 100 } }],
    });
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toMatch(/trusses\[0\]: array/);
    expect(sendCommand).not.toHaveBeenCalled();
  });
});

describe("create_member_array", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockImplementation(okResponse);
  });

  it("sends purlins to create_beams with offsets, slope rotation and purlin usage", async () => {
    const tool = setup()("create_member_array");
    await tool.invoke({
      arrays: [
        {
          familyName: "C-Channel",
          typeName: "C200",
          level: "Roof",
          baseLine: { start: { x: 0, y: 0, z: 0 }, end: { x: 12000, y: 0, z: 0 } },
          farLine: { start: { x: 0, y: 4000, z: 3000 }, end: { x: 12000, y: 4000, z: 3000 } },
          maxSpacing: 1300,
          followSlope: true,
          zJustification: "bottom" as const,
          mark: "P1",
        },
      ],
    });
    const [command, params] = sendCommand.mock.calls[0] as [string, { beams: Array<Record<string, unknown>> }];
    expect(command).toBe("create_beams");
    expect(params.beams).toHaveLength(5);
    expect(params.beams[4]).toEqual({
      familyName: "C-Channel",
      typeName: "C200",
      start: { x: 0, y: 4000 },
      end: { x: 12000, y: 4000 },
      level: "Roof",
      startOffset: 3000,
      endOffset: 3000,
      structuralUsage: "purlin",
      zJustification: "bottom",
      rotationDeg: 36.87,
      mark: "P5",
    });
  });

  it("validates layouts", () => {
    const tool = setup()("create_member_array");
    const base = {
      typeId: 1,
      level: "L1",
      baseLine: { start: { x: 0, y: 0 }, end: { x: 1000, y: 0 } },
      direction: { x: 0, y: 1 },
      distance: 3000,
      spacing: 1000,
    };
    expect(() => tool.parse({ arrays: [base] })).not.toThrow();
    expect(() => tool.parse({ arrays: [{ ...base, count: 3 }] })).toThrow();
    expect(() => tool.parse({ arrays: [{ ...base, distance: undefined }] })).toThrow();
    expect(() => tool.parse({ arrays: [{ ...base, boundary: [{ x: 0, y: 0 }] }] })).toThrow();
  });

  it("is an error when a layout generates nothing", async () => {
    const result = await setup()("create_member_array").invoke({
      arrays: [
        {
          typeId: 1,
          level: "L1",
          baseLine: { start: { x: 0, y: 0 }, end: { x: 1000, y: 0 } },
          direction: { x: 0, y: 1 },
          distance: 3000,
          spacing: 1000,
          boundary: [{ x: 5000, y: 5000 }, { x: 6000, y: 5000 }, { x: 6000, y: 6000 }],
        },
      ],
    });
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toMatch(/arrays\[0\]: generates no members/);
  });

  it("registers both tools in the structure catalog", () => {
    expect(TOOL_MANIFEST.create_trusses.catalogs).toContain("structure");
    expect(TOOL_MANIFEST.create_member_array.catalogs).toContain("structure");
  });
});
