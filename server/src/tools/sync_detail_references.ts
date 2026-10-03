import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";
import {
  detailReferenceOptionsShape,
  sheetDetailPairSchema,
  validationError,
} from "../utils/detailLibrarySchemas.js";

export function registerSyncDetailReferencesTool(server: McpServer) {
  server.tool(
    "sync_detail_references",
    "Rewrite the Detail Number / Sheet Number text parameters of manual detail-reference bubbles (see audit_detail_references). `mappings` points the listed bubbles at a view placed on a sheet: each bubble receives that view's current viewport detail number and sheet number. `renumberFrom` + `to` rewrites every bubble (matched by `familyNameContains`) that currently refers to the old sheet/detail pair, e.g. after renumbering viewports. Both modes can be combined. dryRun (default true) only reports before/after values; set dryRun=false to write, as one undo step with per-bubble results. Type parameters are never written.",
    {
      mappings: z
        .array(
          z
            .object({
              bubbleIds: z.array(elementIdSchema).min(1).max(1000).describe("Bubble family instances to update"),
              targetViewId: elementIdSchema.describe("Placed view the bubbles should reference"),
            })
            .strict()
        )
        .max(500)
        .optional(),
      renumberFrom: sheetDetailPairSchema.optional().describe("Old sheet/detail pair to find"),
      to: sheetDetailPairSchema.optional().describe("New sheet/detail pair to write"),
      ...detailReferenceOptionsShape,
      dryRun: z.boolean().optional().default(true),
    },
    async (args) => {
      if (args.renumberFrom && !args.to) {
        return validationError("sync_detail_references", "'renumberFrom' requires 'to'.");
      }
      if (args.to && !args.renumberFrom) {
        return validationError("sync_detail_references", "'to' requires 'renumberFrom'.");
      }
      if (!args.renumberFrom && (!args.mappings || args.mappings.length === 0)) {
        return validationError("sync_detail_references", "Provide 'mappings' or 'renumberFrom' with 'to'.");
      }
      return sendDocumentationCommand("sync_detail_references", args);
    }
  );
}
