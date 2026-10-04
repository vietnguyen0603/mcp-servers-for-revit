import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { elementIdSchema } from "../utils/documentationSchemas.js";
import { layoutTable, type TableCell, validateCells } from "../utils/tableLayout.js";
import { readIds, resolveViewInfo, type RevitSender, runChunkedBatch } from "../utils/revitBatch.js";

const size = z.number().positive().max(2000);
const styleName = z.string().min(1).max(256);
const index = z.number().int().min(0).max(199);

const cellSchema = z
  .object({
    row: index,
    col: index,
    text: z.string().max(4096),
    rowSpan: z.number().int().min(1).max(200).optional(),
    colSpan: z.number().int().min(1).max(200).optional(),
    align: z.enum(["Left", "Center", "Right"]).optional().describe("Horizontal alignment (default Center)"),
    textType: styleName.optional().describe("Text type name for this cell"),
  })
  .strict();

export const draftedTableShape = {
  viewId: elementIdSchema.optional().describe("Drafting/detail view (default active view)"),
  origin: z
    .object({ x: z.number().finite(), y: z.number().finite() })
    .strict()
    .describe("Top-left corner of the table (view model mm)"),
  columns: z.array(size).min(1).max(50).describe("Column widths, paper mm, left to right"),
  rows: z.array(size).min(1).max(200).describe("Row heights, paper mm, top to bottom"),
  scale: z.number().int().min(1).max(100000).optional().describe("Model mm = paper mm x scale (default the view scale)"),
  cells: z.array(cellSchema).max(2000).describe("Cell texts; rowSpan/colSpan merge cells (no rules drawn inside)"),
  borderStyle: styleName.optional().default("PEN5").describe("Line style of the outer border and heavy rules"),
  ruleStyle: styleName.optional().default("PEN1").describe("Line style of the other rules"),
  heavyRows: z.array(z.number().int().min(1).max(199)).max(200).optional().describe("Rows whose top rule uses borderStyle"),
  heavyCols: z.array(z.number().int().min(1).max(49)).max(50).optional().describe("Columns whose left rule uses borderStyle"),
  textType: styleName.optional().default('1/8" Arial').describe("Default text type name"),
  textHeightMm: z.number().positive().max(50).optional().describe("Paper text height for vertical centring (default parsed from the type name, else 3.175)"),
  paddingMm: z.number().min(0).max(50).optional().describe("Horizontal cell padding, paper mm (default 1.5)"),
  title: z.object({ text: z.string().min(1).max(4096), textType: styleName.optional() }).strict().optional(),
};

type DraftedTableArgs = z.infer<z.ZodObject<typeof draftedTableShape>>;

export interface DraftedTableResult {
  viewId?: number;
  scale?: number;
  size?: { widthMm: number; heightMm: number };
  created: { lines?: string; texts?: string };
  lineIds: number[];
  textNoteIds: number[];
  failures: Array<{ kind: "view" | "lines" | "texts"; index?: number; cell?: number; message: string }>;
}

export async function runDraftedTable(client: RevitSender, args: DraftedTableArgs): Promise<DraftedTableResult> {
  const result: DraftedTableResult = { created: {}, lineIds: [], textNoteIds: [], failures: [] };

  let viewId = args.viewId;
  let scale = args.scale;
  if (viewId === undefined || scale === undefined) {
    const info = await resolveViewInfo(client, viewId, scale === undefined);
    viewId = info.viewId;
    scale = scale ?? info.scale;
  }
  result.viewId = viewId;
  if (!scale) {
    result.failures.push({ kind: "view", message: "Could not read the view scale; pass scale" });
    return result;
  }
  result.scale = scale;

  const layout = layoutTable({
    origin: args.origin,
    columns: args.columns,
    rows: args.rows,
    scale,
    cells: args.cells as TableCell[],
    borderStyle: args.borderStyle ?? "PEN5",
    ruleStyle: args.ruleStyle ?? "PEN1",
    heavyRows: args.heavyRows,
    heavyCols: args.heavyCols,
    textType: args.textType ?? '1/8" Arial',
    textHeightMm: args.textHeightMm,
    paddingMm: args.paddingMm,
    title: args.title,
  });
  result.size = { widthMm: layout.width, heightMm: layout.height };

  const lineOutcomes = await runChunkedBatch(client, "create_detail_lines", "lines", layout.lines, { viewId });
  let lineCount = 0;
  lineOutcomes.forEach((outcome, i) => {
    const ids = outcome.success ? readIds(outcome.data, "detailCurveIds") : [];
    if (ids.length === 0) result.failures.push({ kind: "lines", index: i, message: outcome.message ?? "no line created" });
    else {
      lineCount++;
      result.lineIds.push(...ids);
    }
  });
  result.created.lines = `${lineCount}/${layout.lines.length}`;

  if (layout.notes.length > 0) {
    const notes = layout.notes.map((note) => ({
      text: note.text,
      location: note.location,
      viewId,
      textNoteTypeName: note.textType,
      width: note.width,
      horizontalAlignment: note.horizontalAlignment,
    }));
    const noteOutcomes = await runChunkedBatch(client, "create_text_note", "notes", notes);
    let textCount = 0;
    noteOutcomes.forEach((outcome, i) => {
      const ids = outcome.success ? readIds(outcome.data, "textNoteId") : [];
      const cell = layout.notes[i].cell;
      if (ids.length === 0)
        result.failures.push({ kind: "texts", index: i, ...(cell >= 0 ? { cell } : {}), message: outcome.message ?? "no text created" });
      else {
        textCount++;
        result.textNoteIds.push(...ids);
      }
    });
    result.created.texts = `${textCount}/${layout.notes.length}`;
  }
  return result;
}

export function registerCreateDraftedTableTool(server: McpServer) {
  server.tool(
    "create_drafted_table",
    "Draw a table (schedule-like grid) with detail lines and text notes in a drafting/detail view - for drafted schedules such as rebar, anchor or hold-down tables on typical-detail sheets. origin is the top-left corner in view model mm; column widths and row heights are paper mm (model = paper x scale, default the view scale). Cells may merge with rowSpan/colSpan: rules are not drawn inside merged cells. The outer border and rules listed in heavyRows (top rule of that row index) / heavyCols (left rule of that column) use borderStyle (default PEN5), other rules ruleStyle (default PEN1). Cell text is wrapped to the cell width minus padding, aligned per cell (default Center) and centred vertically by an estimate of the wrapped line count. Optional title is centred above the table. " +
      'Example: {"origin":{"x":0,"y":0},"columns":[20,30,30],"rows":[8,6,6],"heavyRows":[1],"cells":[{"row":0,"col":0,"colSpan":3,"text":"HOLD-DOWN SCHEDULE"},{"row":1,"col":0,"text":"MARK"},{"row":1,"col":1,"text":"MODEL"}]}. ' +
      "Returns viewId, scale, model size, created counts, lineIds, textNoteIds and per-item failures.",
    draftedTableShape,
    async (args) => {
      const errors = validateCells(args.rows.length, args.columns.length, args.cells as TableCell[]);
      if (errors.length > 0) {
        return { content: [{ type: "text" as const, text: `create_drafted_table: ${errors.join("; ")}` }], isError: true };
      }
      try {
        const result = await withRevitConnection((client) => runDraftedTable(client, args as DraftedTableArgs));
        const nothing = result.lineIds.length === 0 && result.textNoteIds.length === 0;
        return {
          content: [{ type: "text" as const, text: JSON.stringify(result, null, 2) }],
          ...(nothing ? { isError: true } : {}),
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `create_drafted_table failed: ${error instanceof Error ? error.message : String(error)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
