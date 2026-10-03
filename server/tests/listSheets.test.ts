import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { registerListSheetsTool } from "../src/tools/list_sheets.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerListSheetsTool(server);
  return getTool("list_sheets");
}

describe("list_sheets", () => {
  beforeEach(() => resetConnectionMock());

  it("keeps the previous defaults and omits offset unless given", async () => {
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: { total: 0, sheets: [] } });
    await setup().invoke({});
    expect(sendCommand).toHaveBeenLastCalledWith("list_sheets", { includeContents: true, limit: 500 });
  });

  it("forwards offset/limit paging", async () => {
    sendCommand.mockResolvedValue({ Success: true, Message: "ok", Response: { total: 104, sheets: [] } });
    await setup().invoke({ offset: 50, limit: 25, includeContents: false });
    expect(sendCommand).toHaveBeenLastCalledWith("list_sheets", { offset: 50, limit: 25, includeContents: false });
  });

  it("rejects negative or fractional offsets", () => {
    const tool = setup();
    expect(() => tool.parse({ offset: -1 })).toThrow();
    expect(() => tool.parse({ offset: 1.5 })).toThrow();
  });
});
