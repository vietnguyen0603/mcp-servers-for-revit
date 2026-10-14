import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export const purgeCadLeftoversShape = {
  include: z
    .array(z.enum(["cadTypes", "filledRegionTypes", "fillPatterns"]))
    .min(1)
    .max(3)
    .optional()
    .describe(
      "What to purge (default [\"cadTypes\",\"filledRegionTypes\"]; fillPatterns is opt-in): cadTypes = CAD import/link types without instances; filledRegionTypes / fillPatterns = imported ones matching namePatterns"
    ),
  namePatterns: z
    .array(z.string().min(1).max(256))
    .min(1)
    .max(50)
    .optional()
    .describe('Name wildcards for imported filled region types and fill patterns (default ["*.dwg","*.dwg-*","*.dwg *","*.dxf","*.dxf-*","*.dxf *"])'),
  remapTo: z
    .union([z.string().min(1).max(256), elementIdSchema])
    .optional()
    .describe("Filled region type (name or id) to switch regions on matched types to first, so those types become unused and are purged"),
  dryRun: z.boolean().optional().default(true).describe("Default true: run, report and roll back. Set false to delete"),
};

export function registerPurgeCadLeftoversTool(server: McpServer) {
  server.tool(
    "purge_cad_leftovers",
    'Clean up CAD import leftovers: delete CAD import/link types (CADLinkType) that have no instance, and imported filled region types (and, opt-in via include, fill patterns) named like "*.dwg-N" that are unused. ' +
      "A fill pattern counts as used when a filled region type, a material, or a view/template filter or category (incl. subcategory) override references it; element-level graphic overrides are NOT checked. " +
      "Only unused types are deleted unless remapTo names a native filled region type, in which case regions on the matched types are switched to it first. Also reports duplicate groups (e.g. Concrete.dwg-1, Concrete.dwg-2, and whether a native 'Concrete' exists). " +
      "dryRun defaults to true: the purge is executed and rolled back so the report is exact; pass dryRun:false to apply in one undo step.",
    purgeCadLeftoversShape,
    async (args) => sendDocumentationCommand("purge_cad_leftovers", args, 300000)
  );
}
