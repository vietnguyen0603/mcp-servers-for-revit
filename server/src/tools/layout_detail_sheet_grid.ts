import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { elementIdSchema, point2Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { detailReferenceOptionsShape, validationError } from "../utils/detailLibrarySchemas.js";
import {
  describePlan,
  planGrid,
  type Border,
  type GridPlan,
  type MeasuredSheet,
} from "../utils/detailSheetGrid.js";

const COMMAND = "layout_detail_sheet_grid";
const MEASURE_TIMEOUT_MS = 180000;
const APPLY_TIMEOUT_MS = 300000;

const sheetNumberSchema = z.string().min(1).max(64);

const seriesSchema = z
  .object({
    prefix: z.string().min(1).max(64).describe("Number prefix of the new sheets, e.g. 'S8-0' for S8-001, S8-002 ..."),
    start: z.number().int().min(0).max(99999).optional().describe("First sheet's suffix (default: the lowest existing one)"),
    digits: z.number().int().min(1).max(6).optional().describe("Zero-padded suffix length (default: from the first sheet)"),
    name: z.string().min(1).max(256).optional().describe("Sheet name (default: the first sheet's name)"),
    sheetNumbers: z
      .array(sheetNumberSchema)
      .max(500)
      .optional()
      .describe("Selected sheets in this series (default: selected sheets whose number starts with prefix)"),
  })
  .strict();

interface RevitEnvelope {
  success?: boolean;
  Success?: boolean;
  message?: string;
  Message?: string;
  response?: unknown;
  Response?: unknown;
}

interface MeasureResponse {
  border: Border;
  borderSource?: string;
  titleBlock?: unknown;
  sheets: MeasuredSheet[];
  otherSheetNumbers?: string[];
  warnings?: string[];
}

function unwrap(result: unknown): { ok: boolean; message: string; body: unknown } {
  const r = (result ?? {}) as RevitEnvelope;
  const ok = r.success !== false && r.Success !== false;
  return { ok, message: r.message ?? r.Message ?? "", body: r.response ?? r.Response };
}

function text(value: unknown, isError = false) {
  return {
    content: [{ type: "text" as const, text: typeof value === "string" ? value : JSON.stringify(value, null, 2) }],
    ...(isError ? { isError: true } : {}),
  };
}

export function registerLayoutDetailSheetGridTool(server: McpServer) {
  server.tool(
    "layout_detail_sheet_grid",
    "Re-lay out a series of typical-detail sheets on the office 12-cell grid: cols x rows (default 4 x 3) RELATIVE cells inside the title block's inner border, numbered 1.. from the top-left, left to right then down. " +
      "A detail's number = the cell holding its title (the bottom-left cell of its block); titles sit bottom-left, aligned per row; title line = max(one cell, drawing width, title text), never the stretched block; the drawing starts at the title's left edge. " +
      "Column widths / row heights flex per sheet so details fill compactly from cell 1 on as few sheets as possible; nothing crosses the inner border. No-Title viewports overlapping a titled one are composite sub-views (numbered 9A, 9B ...). " +
      "Modes: 'measure' (read-only: inner border from the title-block family lines, or `border`, plus every viewport's box and title size), 'plan' (read-only: packs on the server and returns per-sheet occupancy maps, detail numbers and the old->new 'detail/sheet' map), " +
      "'apply' (re-plans identically, then moves viewports (recreating those that change sheet), sets title offsets/line lengths (Revit 2022+) and detail numbers via temporary numbers, creates new sheets on the same title block, remaps Section Cut bubbles and 'n/SHEET' / 'DETAIL n ON THIS SHEET' text, deletes selected sheets left empty; dryRun defaults to true), " +
      "'verify' (read-only: OUT of border, OVL overlaps, TMP leftover numbers, BAD text/bubble references -> allClear). Select sheets with sheetIds, sheetNumberPrefixes or sheetNumberPattern. The active view cannot be deleted: activate a kept sheet before apply.",
    {
      mode: z.enum(["measure", "plan", "apply", "verify"]).default("plan"),
      sheetIds: z.array(elementIdSchema).max(500).optional().describe("Sheets to lay out"),
      sheetNumberPrefixes: z.array(sheetNumberSchema).max(50).optional().describe("Sheets whose number starts with one of these"),
      sheetNumberPattern: z
        .string()
        .min(1)
        .max(256)
        .optional()
        .describe(".NET regex matched against sheet numbers, e.g. '^S8-0\\d\\d$'"),
      border: z
        .object({ min: point2Schema, max: point2Schema })
        .strict()
        .optional()
        .describe("Inner border override in sheet mm (default: read from the title block family)"),
      cols: z.number().int().min(1).max(8).default(4),
      rows: z.number().int().min(1).max(6).default(3),
      pad: z.number().finite().min(0).max(50).default(4).describe("Clearance inside each block (mm)"),
      gap: z.number().finite().min(0).max(50).default(3).describe("Gap between a drawing and its title (mm)"),
      tries: z.number().int().min(0).max(5000).default(300).describe("Random orderings tried per series (more = tighter, slower)"),
      series: z
        .array(seriesSchema)
        .max(50)
        .optional()
        .describe("Sheet series and numbering; default groups by number prefix + sheet name, numbered from the lowest existing sheet"),
      dryRun: z.boolean().default(true).describe("apply: do everything, report, then roll back"),
      deleteEmptySheets: z.boolean().default(true).describe("apply: delete selected sheets left without viewports or other content"),
      remapReferences: z.boolean().default(true).describe("apply: rewrite bubbles and text references with the old->new map"),
      textSheetPattern: z
        .string()
        .min(1)
        .max(256)
        .optional()
        .describe(".NET regex for sheet numbers in text references 'n/SHEET' (default: the selected sheet numbers)"),
      ...detailReferenceOptionsShape,
      includeViewports: z.boolean().default(false).describe("measure/plan: include the full per-viewport tables"),
    },
    async (args) => {
      if (!args.sheetIds?.length && !args.sheetNumberPrefixes?.length && !args.sheetNumberPattern) {
        return validationError(COMMAND, "Select sheets with sheetIds, sheetNumberPrefixes or sheetNumberPattern.");
      }
      const selection = {
        sheetIds: args.sheetIds,
        sheetNumberPrefixes: args.sheetNumberPrefixes,
        sheetNumberPattern: args.sheetNumberPattern,
        border: args.border,
      };
      const references = {
        textSheetPattern: args.textSheetPattern,
        familyNameContains: args.familyNameContains,
        detailNumberParam: args.detailNumberParam,
        sheetNumberParam: args.sheetNumberParam,
      };

      if (args.mode === "verify") {
        return sendDocumentationCommand(COMMAND, { mode: "verify", ...selection, ...references }, MEASURE_TIMEOUT_MS);
      }

      try {
        return await withRevitConnection(async (client) => {
          const measured = unwrap(await client.sendCommand(COMMAND, { mode: "measure", ...selection }, MEASURE_TIMEOUT_MS));
          if (!measured.ok) return text(`${COMMAND} measure failed: ${measured.message}`, true);
          const m = measured.body as MeasureResponse;
          if (!m?.sheets?.length) return text(`${COMMAND}: no sheets matched the selection.`, true);

          if (args.mode === "measure") return text(describeMeasure(m, args.includeViewports));

          const plan = planGrid(
            m.sheets,
            m.border,
            { cols: args.cols, rows: args.rows, pad: args.pad, gap: args.gap, tries: args.tries },
            args.series,
            m.otherSheetNumbers ?? []
          );
          if (plan.errors.length) return text({ message: "No plan: some details do not fit one sheet.", errors: plan.errors }, true);
          const summary = describePlanResult(m, plan, args.includeViewports);
          if (args.mode === "plan") return text(summary);

          const applied = unwrap(
            await client.sendCommand(
              COMMAND,
              {
                mode: "apply",
                dryRun: args.dryRun,
                deleteEmptySheets: args.deleteEmptySheets,
                remapReferences: args.remapReferences,
                ...references,
                oldSheetIds: m.sheets.map((s) => s.id),
                sheets: plan.sheets.map((s) => ({
                  number: s.number,
                  name: s.name,
                  sheetId: s.sheetId,
                  templateSheetId: s.templateSheetId,
                  viewports: s.viewports,
                })),
              },
              APPLY_TIMEOUT_MS
            )
          );
          const { oldToNew: _map, ...brief } = summary;
          return text(
            { ...brief, apply: { success: applied.ok, message: applied.message, result: applied.body } },
            !applied.ok
          );
        });
      } catch (error) {
        return text(`${COMMAND} failed: ${error instanceof Error ? error.message : String(error)}`, true);
      }
    }
  );
}

function describeMeasure(m: MeasureResponse, full: boolean) {
  const b = m.border;
  return {
    border: b,
    borderSource: m.borderSource,
    titleBlock: m.titleBlock,
    sheets: m.sheets.map((s) => {
      const titled = s.viewports.filter((v) => v.titled).length;
      return `${s.number} '${s.name}': ${s.viewports.length} viewports (${titled} titled)`;
    }),
    ...(m.warnings?.length ? { warnings: m.warnings } : {}),
    ...(full ? { viewports: m.sheets } : {}),
  };
}

function describePlanResult(m: MeasureResponse, plan: GridPlan, full: boolean) {
  const created = plan.sheets.filter((s) => !s.sheetId).length;
  const moved = Object.keys(plan.oldToNew).length;
  return {
    summary:
      `${plan.sheets.length} sheets (was ${m.sheets.length}; ${created} new, ${plan.emptiedSheets.length} emptied), ` +
      `${plan.sheets.reduce((a, s) => a + s.viewports.length, 0)} viewports, ${moved} detail references change`,
    border: m.border,
    sheets: describePlan(plan),
    ...(plan.emptiedSheets.length ? { emptiedSheets: plan.emptiedSheets.map((s) => s.number) } : {}),
    oldToNew: plan.oldToNew,
    ...(m.warnings?.length ? { warnings: m.warnings } : {}),
    ...(full ? { plan: plan.sheets } : {}),
  };
}
