import { mkdtempSync, writeFileSync } from "fs";
import { tmpdir } from "os";
import path from "path";
import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../src/utils/ConnectionManager.js", async () =>
  await import("./helpers/connectionMock.js")
);

import { registerCaptureViewTool } from "../src/tools/capture_view.js";
import { resetConnectionMock, sendCommand } from "./helpers/connectionMock.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

function setup() {
  const { server, getTool } = createFakeMcpServer();
  registerCaptureViewTool(server);
  return getTool("capture_view");
}

describe("capture_view", () => {
  beforeEach(() => {
    resetConnectionMock();
  });

  it("dispatches with defaults and returns the PNG as image content", async () => {
    const file = path.join(mkdtempSync(path.join(tmpdir(), "capture-")), "capture.png");
    writeFileSync(file, Buffer.from([0x89, 0x50, 0x4e, 0x47]));
    sendCommand.mockResolvedValue({ Success: true, Message: "Captured", Response: { file, viewId: 7 } });

    const result = (await setup().invoke({})) as unknown as {
      content: Array<{ type: string; data?: string; mimeType?: string }>;
      isError?: boolean;
    };

    expect(sendCommand).toHaveBeenCalledWith("capture_view", { zoomToFit: true, pixelSize: 1600 });
    expect(result.isError).toBeUndefined();
    expect(result.content[0]).toEqual({ type: "image", data: "iVBORw==", mimeType: "image/png" });
    expect(result.content[1].type).toBe("text");
  });

  it("flags Revit failures as errors without reading a file", async () => {
    sendCommand.mockResolvedValue({ Success: false, Message: "View 9 was not found." });
    const result = await setup().invoke({ viewId: 9 });
    expect(result.isError).toBe(true);
    expect(result.content).toHaveLength(1);
  });

  it("forwards restoreActiveView only when given", async () => {
    sendCommand.mockResolvedValue({ Success: false, Message: "x" });
    await setup().invoke({ viewId: 5, restoreActiveView: true });
    expect(sendCommand).toHaveBeenCalledWith("capture_view", {
      viewId: 5,
      zoomToFit: true,
      pixelSize: 1600,
      restoreActiveView: true,
    });
    expect(() => setup().parse({ restoreActiveView: "yes" })).toThrow();
  });

  it("validates input bounds", () => {
    const tool = setup();
    expect(() => tool.parse({ pixelSize: 8000 })).toThrow();
    expect(() => tool.parse({ folder: "relative/dir" })).toThrow(/absolute/);
    expect(tool.parse({ viewId: 1, zoomToFit: false, folder: "C:\\Captures" })).toBeTruthy();
  });
});
