import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point2Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerCopyViewContentsTool(server: McpServer) {
  server.tool(
    "copy_view_contents",
    "Copy view-specific elements (detail lines, filled regions, detail components, text, dimensions, detail groups...) from a source view into a target view of the active document. Without `elementIds` every copyable element owned by the source view is copied (view markers, sketch lines, group members and nested sub-components are skipped; live view references are skipped across documents). `sourceDocumentTitle` names another OPEN document (title with or without .rvt) holding the source view, e.g. a typical-detail library, for library-to-project transfer; types with the same name in the destination are reused. `offset` moves the copies along the target view's right/up directions (mm). One undo step.",
    {
      sourceViewId: elementIdSchema.describe("View to copy from (in the source document)"),
      targetViewId: elementIdSchema.describe("View in the active document to copy into"),
      elementIds: z
        .array(elementIdSchema)
        .min(1)
        .max(10000)
        .optional()
        .describe("Specific source-view elements; default all copyable ones"),
      offset: point2Schema.optional().describe("Offset in the target view plane (mm)"),
      sourceDocumentTitle: z
        .string()
        .min(1)
        .max(260)
        .optional()
        .describe("Title of another open document holding the source view; default the active document"),
    },
    async (args) => sendDocumentationCommand("copy_view_contents", args)
  );
}
