import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema } from "../utils/documentationSchemas.js";
import { detailReferenceOptionsShape } from "../utils/detailLibrarySchemas.js";
import { AUDIT_CHECKS, errorText, requestRevit, textResult } from "../utils/draftingLibrary.js";

const allowedNamesSchema = z
  .array(z.string().min(1).max(256))
  .min(1)
  .max(500)
  .optional();

export function registerAuditDetailLibraryTool(server: McpServer) {
  server.tool(
    "audit_detail_library",
    "QA the typical-detail library (all drafting views, or the views in scope) and return a summary (issue count per check) plus details (up to limitPerCheck rows each). Checks: references (manual 'Section Cut' bubbles classified exactly like audit_detail_references; dead ones listed, plus ok/placeholder counts and dead targets by sheet), placement (unplaced views), naming (views not matching standards.namePattern, and likely accidental duplicates such as 'X Copy 1' / 'X2' when 'X' exists), lineStyles / textTypes / dimensionTypes / filledRegionTypes (non-standard: not in the standards.<kind> allowed list when given, else used fewer than standards.minUses times; uses, views, sample ids), cadImports (DWG import instances per view, '*.dwg-N' fill types in use / unused, IMPORT-* line patterns), textOverlaps (text notes overlapping by at least overlapMinAreaMm2 of paper), dimensionOverrides (overrides that look like a number and hide the real value; '- Blank' type count) and drawingList (empty sheets, sheets off the sheet list, duplicate sheet names, detail-number gaps). " +
      "Allowed-name entries are exact names, globs with * and ?, or /regex/. Fix with sync_detail_references, modify_annotations setLineStyle/setType, find_tag_overlaps. Read-only.",
    {
      checks: z
        .array(z.enum(AUDIT_CHECKS))
        .min(1)
        .max(AUDIT_CHECKS.length)
        .optional()
        .describe("Checks to run (default all)"),
      viewIds: z.array(elementIdSchema).max(5000).optional().describe("Audit these views instead of all drafting views"),
      sheetNumbers: z
        .array(z.string().min(1).max(64))
        .max(500)
        .optional()
        .describe("Only views placed on these sheets (drawingList: only these sheets)"),
      viewNameContains: z.string().min(1).max(256).optional().describe("Only views whose name contains this (case-insensitive)"),
      standards: z
        .object({
          lineStyles: allowedNamesSchema.describe("Allowed line style names"),
          textTypes: allowedNamesSchema.describe("Allowed text note type names"),
          dimensionTypes: allowedNamesSchema.describe("Allowed dimension type names"),
          filledRegionTypes: allowedNamesSchema.describe("Allowed filled region type names"),
          namePattern: z
            .string()
            .min(1)
            .max(500)
            .optional()
            .describe("View naming regex (.NET, case-insensitive; default '^[A-Z]{2,4}-[A-Z]{1,5}-\\d+[A-Z]?(-[A-Z]+)?_')"),
          minUses: z
            .number()
            .int()
            .min(1)
            .max(100000)
            .optional()
            .describe("Without an allowed list, styles/types used fewer times are flagged (default 5)"),
        })
        .strict()
        .optional(),
      ...detailReferenceOptionsShape,
      placeholders: z
        .array(z.string().max(64))
        .max(50)
        .optional()
        .describe("Bubble values treated as placeholders (default '', '-', '--', 'X', 'XX', 'S-000')"),
      overlapMinAreaMm2: z
        .number()
        .min(0)
        .max(10000)
        .optional()
        .describe("Minimum text overlap in paper mm² (default 1)"),
      limitPerCheck: z.number().int().min(1).max(1000).optional().default(25),
      summaryOnly: z.boolean().optional().default(false).describe("Return counts only, without detail rows"),
    },
    async (args) => {
      const { summaryOnly, ...params } = args;
      try {
        const outcome = await requestRevit<Record<string, any>>("audit_detail_library", params);
        if (!outcome.success || !outcome.body) {
          return textResult(JSON.stringify(outcome.raw, null, 2), true);
        }
        if (summaryOnly && outcome.body.details) {
          for (const section of Object.values(outcome.body.details as Record<string, Record<string, unknown>>)) {
            delete section.items;
          }
        }
        return textResult(JSON.stringify(outcome.raw, null, 2));
      } catch (error) {
        return errorText("audit_detail_library", error);
      }
    }
  );
}
