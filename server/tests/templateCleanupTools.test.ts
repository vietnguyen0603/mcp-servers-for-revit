import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { TOOL_MANIFEST } from "../src/catalog/toolManifest.js";
import { registerMergeDuplicateFamiliesTool } from "../src/tools/merge_duplicate_families.js";
import { registerRenameFamiliesTypesTool } from "../src/tools/rename_families_types.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerRenameFamiliesTypesTool(server);
  registerMergeDuplicateFamiliesTool(server);
  return getTool;
}

describe("template clean-up tools", () => {
  beforeEach(() => {
    resetConnectionMock();
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: { counts: {} } });
  });

  it("forwards rename_families_types with dry-run defaults", async () => {
    const tool = setup()("rename_families_types");
    await tool.invoke({ find: "TT", replace: "AC" });
    expect(sendCommand).toHaveBeenCalledWith(
      "rename_families_types",
      {
        find: "TT",
        replace: "AC",
        regex: false,
        matchCase: true,
        target: "both",
        includeSystemTypes: true,
        alsoRename: [],
        dryRun: true,
        maxResults: 1000,
      },
      300000
    );
  });

  it("accepts a regex prefix strip on types with filters", () => {
    const tool = setup()("rename_families_types");
    expect(() =>
      tool.parse({
        find: "^TT-",
        regex: true,
        target: "type",
        categories: ["Callout Heads"],
        familyNamePattern: ["_TT-*", "_AC-*"],
        alsoRename: ["linePatterns", "lineStyles"],
        dryRun: false,
      })
    ).not.toThrow();
  });

  it("rejects an empty find, unknown target and unknown alsoRename kind", () => {
    const tool = setup()("rename_families_types");
    expect(() => tool.parse({ find: "" })).toThrow();
    expect(() => tool.parse({ find: "TT", target: "families" })).toThrow();
    expect(() => tool.parse({ find: "TT", alsoRename: ["views"] })).toThrow();
  });

  it("forwards merge_duplicate_families with dry-run defaults", async () => {
    const tool = setup()("merge_duplicate_families");
    await tool.invoke({ categories: ["Callout Heads"], preferNamePattern: "*AC*" });
    expect(sendCommand).toHaveBeenCalledWith(
      "merge_duplicate_families",
      {
        categories: ["Callout Heads"],
        preferNamePattern: "*AC*",
        includeSystemTypes: false,
        force: false,
        maxFamilyOpens: 300,
        dryRun: true,
        maxResults: 500,
      },
      600000
    );
  });

  it("accepts explicit pairs by id, 'Family : Type' and family name", () => {
    const tool = setup()("merge_duplicate_families");
    expect(() =>
      tool.parse({
        pairs: [
          { keep: 123456, merge: 654321 },
          { keep: "_AC-SY-Callout Head : Callout Head", merge: "_TT-SY-Callout Head : TT-Callout Head" },
          { keep: "_AC-SY-Section Head", merge: "_TT-SY-Section Head" },
        ],
        force: true,
        dryRun: false,
      })
    ).not.toThrow();
    expect(() => tool.parse({ pairs: [{ keep: 1 }] })).toThrow();
    expect(() => tool.parse({ pairs: [] })).toThrow();
  });

  it("refuses force without pairs", async () => {
    const tool = setup()("merge_duplicate_families");
    const result = await tool.invoke({ force: true });
    expect(result.isError).toBe(true);
    expect(sendCommand).not.toHaveBeenCalled();
  });

  it("lists both tools in the manifest, merge as destructive", () => {
    expect(TOOL_MANIFEST.rename_families_types.readOnly).toBe(false);
    expect(TOOL_MANIFEST.merge_duplicate_families.readOnly).toBe(false);
    expect(TOOL_MANIFEST.merge_duplicate_families.destructive).toBe(true);
  });
});
