import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

const ABSOLUTE = /^([a-zA-Z]:[\\/]|\\\\|\/)/;
const REVIT_FILE = /\.(rvt|rfa|rte|rft)$/i;

function absolutePath(field: string, extensions: RegExp, label: string) {
  return z
    .string()
    .min(1)
    .max(1024)
    .regex(ABSOLUTE, `${field} must be an absolute path`)
    .regex(extensions, `${field} must end with ${label}`);
}

const openDocumentShape = {
  path: absolutePath("path", REVIT_FILE, ".rvt, .rfa, .rte or .rft")
    .optional()
    .describe("Model to open (absolute path). Omit to create a new project"),
  templatePath: absolutePath("templatePath", /\.rte$/i, ".rte")
    .optional()
    .describe("New project: template to start from (default Revit's default project template)"),
  saveAsPath: absolutePath("saveAsPath", REVIT_FILE, ".rvt, .rfa, .rte or .rft")
    .optional()
    .describe(
      "New project: where to save it (required). With path: save the opened model as this copy first and open the copy (e.g. a detached central)"
    ),
  overwrite: z.boolean().optional().describe("Replace an existing file at saveAsPath (default false)"),
  activate: z
    .boolean()
    .optional()
    .describe("Open in the Revit UI as the active document so the other tools work on it (default true); false opens it in the background"),
  detach: z.boolean().optional().describe("Workshared central model: open detached from central, preserving worksets"),
};

export const openDocumentSchema = z
  .object(openDocumentShape)
  .strict()
  .refine((a) => a.path === undefined || a.templatePath === undefined, {
    message: "Give path (open a model) or templatePath (new project), not both",
  })
  .refine((a) => a.path !== undefined || a.saveAsPath !== undefined, {
    message: "A new project needs saveAsPath (Revit opens projects in the UI from a file)",
  })
  .refine((a) => a.detach === undefined || a.path !== undefined, { message: "detach applies to path only" })
  .refine((a) => a.overwrite === undefined || a.saveAsPath !== undefined, {
    message: "overwrite applies to saveAsPath only",
  });

export function registerOpenDocumentTool(server: McpServer) {
  server.tool(
    "open_document",
    "Open a Revit model (path) or create a new project from a template (templatePath, default Revit's default project template; saveAsPath required) and make it the active document so the other tools work on it. " +
      "detach opens a workshared central detached (worksets preserved); with path + saveAsPath the model is saved as that copy first (a workshared copy becomes a new central) and the copy is opened. activate:false opens it in the background only. " +
      "Reports title, path, workshared state, whether it is active, and warnings. Opening a large model can take minutes.",
    openDocumentShape,
    async (args) => {
      const parsed = openDocumentSchema.safeParse(args);
      if (!parsed.success) {
        return {
          content: [
            { type: "text" as const, text: `open_document failed: ${parsed.error.issues.map((i) => i.message).join("; ")}` },
          ],
          isError: true,
        };
      }
      const params = { ...parsed.data, activate: parsed.data.activate ?? true };
      return sendDocumentationCommand("open_document", params, 600000);
    }
  );
}
