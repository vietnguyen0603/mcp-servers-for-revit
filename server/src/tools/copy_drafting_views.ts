import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { validationError } from "../utils/detailLibrarySchemas.js";

export function registerCopyDraftingViewsTool(server: McpServer) {
  server.tool(
    "copy_drafting_views",
    "Insert-Views-from-File equivalent: for each drafting view of another OPEN document (e.g. the typical-detail library) create a drafting view in the active document with the same name, scale, detail level and title on sheet, then copy its contents. Types with the same name in the destination are reused. On a view-name clash nameConflict 'skip' (default) skips the view, 'rename' appends ' (2)', ' (3)'... Views are given by `viewIds` and/or exact `viewNames` in the source document. One undo step with per-view results; keep batches to a few dozen views so the call finishes within the 2-minute timeout.",
    {
      sourceDocumentTitle: z.string().min(1).max(260).describe("Title of the open source document (with or without .rvt)"),
      viewIds: z.array(elementIdSchema).max(200).optional().describe("Drafting view ids in the source document"),
      viewNames: z
        .array(z.string().min(1).max(256))
        .max(200)
        .optional()
        .describe("Drafting view names in the source document"),
      nameConflict: z.enum(["skip", "rename"]).optional().default("skip"),
      viewFamilyTypeId: elementIdSchema
        .optional()
        .describe("Drafting view type in the active document; default the first one"),
    },
    async (args) => {
      if ((args.viewIds?.length ?? 0) + (args.viewNames?.length ?? 0) === 0) {
        return validationError("copy_drafting_views", "Provide 'viewIds' or 'viewNames'.");
      }
      return sendDocumentationCommand("copy_drafting_views", args);
    }
  );
}
