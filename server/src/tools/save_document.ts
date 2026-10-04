import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

const ABSOLUTE = /^([a-zA-Z]:[\\/]|\\\\|\/)/;

const saveDocumentShape = {
  saveAsPath: z
    .string()
    .min(1)
    .max(1024)
    .regex(ABSOLUTE, "saveAsPath must be an absolute path")
    .regex(/\.(rvt|rfa|rte|rft)$/i, "saveAsPath must end with .rvt, .rfa, .rte or .rft")
    .optional()
    .describe("Save As: absolute path of the new file; the session continues in it. Omit to save in place"),
  overwrite: z.boolean().optional().describe("Save As: replace an existing file (default false)"),
  compact: z.boolean().optional().describe("Compact the file while saving (slower, smaller file)"),
  asCentral: z
    .boolean()
    .optional()
    .describe("Save As of a workshared model: save it as a new central model (needed for detached workshared models)"),
};

export const saveDocumentSchema = z
  .object(saveDocumentShape)
  .strict()
  .refine((a) => a.saveAsPath !== undefined || (a.overwrite === undefined && a.asCentral === undefined), {
    message: "overwrite and asCentral apply to saveAsPath only",
  });

export function registerSaveDocumentTool(server: McpServer) {
  server.tool(
    "save_document",
    "Save the active Revit document in place, or Save As to saveAsPath (absolute .rvt/.rfa/.rte/.rft path; overwrite replaces an existing file, compact, asCentral for workshared models). " +
      "Reports title, path, workshared state and isModified after saving. A document that was never saved needs saveAsPath. Saving a large model can take minutes.",
    saveDocumentShape,
    async (args) => {
      const parsed = saveDocumentSchema.safeParse(args);
      if (!parsed.success) {
        return {
          content: [
            { type: "text" as const, text: `save_document failed: ${parsed.error.issues.map((i) => i.message).join("; ")}` },
          ],
          isError: true,
        };
      }
      return sendDocumentationCommand("save_document", parsed.data, 600000);
    }
  );
}
