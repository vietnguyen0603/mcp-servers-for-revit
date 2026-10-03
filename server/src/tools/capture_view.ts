import { promises as fs } from "fs";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema } from "../utils/documentationSchemas.js";
import { absoluteFolderSchema } from "../utils/exportSchemas.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

interface CaptureResult {
  success?: boolean;
  Success?: boolean;
  message?: string;
  Message?: string;
  response?: { file?: string };
  Response?: { file?: string };
}

export function registerCaptureViewTool(server: McpServer) {
  server.tool(
    "capture_view",
    "Refresh the active Revit view (or switch to viewId), zoom it to fit and return a PNG screenshot of it, so you can check the result of your work visually. Changes only the zoom and active view, never the model.",
    {
      viewId: elementIdSchema.optional().describe("View or sheet to activate and capture (default active view)"),
      zoomToFit: z.boolean().optional().default(true).describe("Zoom to fit before capturing"),
      pixelSize: z
        .number()
        .int()
        .min(256)
        .max(4096)
        .optional()
        .default(1600)
        .describe("Image width in pixels (256-4096)"),
      restoreActiveView: z
        .boolean()
        .optional()
        .describe(
          "Switch back to the previously active view after capturing (and close the captured view's window if the capture opened it), so the captured view can be deleted afterwards. Default false"
        ),
      folder: absoluteFolderSchema
        .optional()
        .describe("Folder for the PNG (default the system temp folder\\revit-mcp-captures)"),
    },
    async (args) => {
      let result: CaptureResult;
      try {
        result = (await withRevitConnection((revitClient) =>
          revitClient.sendCommand("capture_view", args)
        )) as CaptureResult;
      } catch (error) {
        return {
          content: [{ type: "text" as const, text: `capture_view failed: ${error instanceof Error ? error.message : String(error)}` }],
          isError: true,
        };
      }

      const summary = { type: "text" as const, text: JSON.stringify(result, null, 2) };
      const file = (result?.response ?? result?.Response)?.file;
      if ((result?.success ?? result?.Success) === false || !file) {
        return { content: [summary], isError: true };
      }

      try {
        const data = (await fs.readFile(file)).toString("base64");
        return { content: [{ type: "image" as const, data, mimeType: "image/png" }, summary] };
      } catch (error) {
        return {
          content: [
            summary,
            { type: "text" as const, text: `Could not read ${file}: ${error instanceof Error ? error.message : String(error)}` },
          ],
          isError: true,
        };
      }
    }
  );
}
