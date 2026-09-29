import { describe, expect, it } from "vitest";
import {
  DEFAULT_TOLERANCES,
  REGISTER_REQUEST_MAX_BYTES,
  RegisterOptionError,
  assertRegisterRequestSize,
  buildSharedRegisterParams,
} from "../src/utils/registerSchemas.js";

describe("registerSchemas", () => {
  it("caps requests at the documented 8 KB single-read limit", () => {
    expect(REGISTER_REQUEST_MAX_BYTES).toBe(8192);
  });

  it("resolves shared defaults including millimetre tolerances", () => {
    const params = buildSharedRegisterParams({});

    expect(params).toEqual({
      levelIds: [],
      levelNames: [],
      pageSize: 100,
      designOptionPolicy: "primary",
      includeLinkedModels: false,
      coordinateSystem: "project",
      includeEvidence: true,
      tolerances: DEFAULT_TOLERANCES,
      parameterMap: {},
    });
  });

  it("keeps only supplied optional identifiers", () => {
    const params = buildSharedRegisterParams({
      viewId: "42",
      phaseId: "7",
      cursor: "opaque",
      levelIds: ["1"],
    });

    expect(params.viewId).toBe("42");
    expect(params.phaseId).toBe("7");
    expect(params.cursor).toBe("opaque");
    expect(params.levelIds).toEqual(["1"]);
  });

  it("rejects the active design-option policy without a view scope", () => {
    expect(() =>
      buildSharedRegisterParams({ designOptionPolicy: "active" })
    ).toThrow(RegisterOptionError);

    expect(
      buildSharedRegisterParams({
        designOptionPolicy: "active",
        viewId: "42",
      }).viewId
    ).toBe("42");
  });

  it("measures a compact request and rejects an oversized one", () => {
    const bytes = assertRegisterRequestSize("get_grid_register_data", {
      a: "b",
    });
    expect(bytes).toBeGreaterThan(0);
    expect(bytes).toBeLessThan(REGISTER_REQUEST_MAX_BYTES);

    const oversized = { blob: "x".repeat(REGISTER_REQUEST_MAX_BYTES) };
    expect(() =>
      assertRegisterRequestSize("get_grid_register_data", oversized)
    ).toThrow(/8192-byte single-read limit/);
  });
});
