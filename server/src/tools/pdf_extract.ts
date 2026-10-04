import { z } from "zod";
import fs from "fs/promises";
import path from "path";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { errorResult } from "../utils/imageToolSchemas.js";
import { countsOf, runExtract, type ExtractRequest } from "../utils/pdf/extract.js";

const xy = z.object({ x: z.number().finite(), y: z.number().finite() }).strict();
const regexSchema = z.string().min(1).max(300);
const absPath = z
  .string()
  .min(1)
  .max(400)
  .regex(/^([A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+|\/)/, "must be an absolute path");

const labelMap = z.record(z.string().min(1).max(16), z.number().finite());

export const pdfExtractSchema = {
  path: absPath.describe("Absolute path of a vector PDF (structural drawing set)"),
  page: z.number().int().min(1).max(100000).describe("1-based page number"),
  calibration: z
    .union([
      z
        .object({
          pointA: z.object({ pdf: xy, model: xy }).strict(),
          pointB: z.object({ pdf: xy, model: xy }).strict(),
        })
        .strict(),
      z
        .object({
          grids: z
            .object({ x: labelMap.optional(), y: labelMap.optional() })
            .strict()
            .describe('Grid labels with known model positions, e.g. {x:{"A2":0,"E2":54400}, y:{"5":0,"2":28000}}; x = vertical grid lines, y = horizontal'),
        })
        .strict(),
    ])
    .optional()
    .describe(
      "Page -> model mm. Either two points {pointA:{pdf:{x,y},model:{x,y}}, pointB:...} (PDF points, top-left origin, y down; scale+rotation+offset) or automatic {grids:{x:{...},y:{...}}} which finds the grid bubbles and fits scale+offset (returns residuals). Required for every mode except grids/text."
    ),
  box: z
    .object({ minX: z.number().finite(), minY: z.number().finite(), maxX: z.number().finite(), maxY: z.number().finite() })
    .strict()
    .refine((b) => b.maxX > b.minX && b.maxY > b.minY, { message: "box needs max > min" })
    .optional()
    .describe("Model-mm area to extract from (default: extent of the main plan's grid lines + 1 m)"),
  pdfRegion: z
    .object({ x0: z.number().finite(), y0: z.number().finite(), x1: z.number().finite(), y1: z.number().finite() })
    .strict()
    .refine((r) => r.x1 > r.x0 && r.y1 > r.y0, { message: "pdfRegion needs x1 > x0 and y1 > y0" })
    .optional()
    .describe("Only look for grid bubbles inside this page rectangle (PDF points) - use when several plans share a sheet"),
  extract: z
    .array(z.enum(["grids", "text", "cuts", "beams", "slabOutline", "openings", "rects"]))
    .min(1)
    .max(7)
    .describe(
      "grids: labelled grid lines; text: text items (+regex); cuts: columns & walls from section-cut gray fills; beams: labelled beams (WxH from label or on-sheet schedule) between parallel edges; slabOutline: outer slab edge polygon + area; openings: dashed-X openings inside the outline; rects: closed rectangles of given sizes (e.g. barrette piles)"
    ),
  options: z
    .object({
      grids: z
        .object({
          clipToBox: z.boolean().optional().describe("Clip grid lines to the box (default: true when box is given, else the drawn extent)"),
          bubbleMinSize: z.number().positive().optional().describe("Min bubble diameter in PDF points (default 10)"),
          bubbleMaxSize: z.number().positive().optional().describe("Max bubble diameter in PDF points (default 60)"),
        })
        .strict()
        .optional(),
      text: z.object({ pattern: regexSchema.optional().describe("Regex filter, e.g. ^C\\d") }).strict().optional(),
      cuts: z
        .object({
          fillGray: z.number().min(0).max(1).optional().describe("Cut fill gray 0..1 (default: the most common mid-gray in the box)"),
          resolution: z.number().min(2).max(200).optional().describe("Raster mm/pixel (default 10, coarsened for big boxes)"),
          labelPattern: regexSchema.optional().describe("Column mark regex (default ^(C\\d+[A-Z]?\\d?|CW\\d+[A-Z]?|WB?\\d*)$)"),
          labelMaxDist: z.number().positive().optional().describe("Max mark distance from a column centre, mm (default 2500)"),
          minThickness: z.number().positive().optional().describe("Min wall thickness mm (default 150)"),
          maxThickness: z.number().positive().optional().describe("Max wall thickness mm (default 1900)"),
          includePolygons: z.boolean().optional().describe("Also return every cut polygon"),
        })
        .strict()
        .optional(),
      beams: z
        .object({
          labelPattern: regexSchema.optional().describe("Beam label regex; group 3/4 or a trailing -WxH give the size"),
          sizes: z.record(z.string().min(1).max(64), z.tuple([z.number().positive(), z.number().positive()])).optional().describe("Extra name -> [W,H] mm"),
          reach: z.number().positive().max(200000).optional().describe("Search half-length along the beam from its label, mm (default 20000)"),
        })
        .strict()
        .optional(),
      slabOutline: z
        .object({
          minStrokeWidth: z.number().min(0).max(10).optional().describe("Min edge stroke width in PDF points (default 0.55)"),
          resolution: z.number().min(2).max(200).optional(),
        })
        .strict()
        .optional(),
      openings: z
        .object({
          minSize: z.number().positive().optional().describe("Min opening side, mm (default 400)"),
          insideOutline: z.boolean().optional().describe("Keep only openings inside the slab outline (default true)"),
        })
        .strict()
        .optional(),
      rects: z
        .object({
          sizes: z.array(z.tuple([z.number().positive(), z.number().positive()])).min(1).max(20).describe("[length,width] mm pairs, e.g. [[2800,1200],[2800,1500]]"),
          tolerance: z.number().positive().optional().describe("Size tolerance mm (default 70)"),
          minStrokeWidth: z.number().min(0).optional().describe("Min edge stroke width in PDF points (default 0.4)"),
          labelPattern: regexSchema.optional().describe("Mark regex, e.g. ^SGBR-"),
          labelMaxDist: z.number().positive().optional().describe("Max mark distance mm (default 3500)"),
        })
        .strict()
        .optional(),
    })
    .strict()
    .optional(),
  outFile: absPath
    .regex(/\.json$/i, "outFile must end with .json")
    .optional()
    .describe("Write the full JSON result here and return only counts (results can be thousands of items)"),
};

const INLINE_LIMIT = 120_000;

export function registerPdfExtractTool(server: McpServer) {
  server.tool(
    "pdf_extract",
    "Read a vector structural drawing PDF page and return model coordinates in mm (read-only; runs in the MCP server, no Revit call). " +
      "Calibrate with two points or automatically from grid bubbles ({grids:{x:{A2:0,E2:54400},y:{'5':0,'2':28000}}}), then extract: grids (labelled lines incl. inclined), text, cuts (columns with b/h/rotation/mark and walls as centre lines + thickness from gray section fills), " +
      "beams (labels like L5-18.HB6-2000x450 or schedule names -> start/end between parallel edges), slabOutline (+area), openings (dashed-X rectangles), rects (e.g. barrettes). " +
      "Use outFile for big results. Feed the output to create_grids / create_structural_columns / create_beams / create_slabs.",
    pdfExtractSchema,
    async (args) => {
      try {
        const stat = await fs.stat(args.path).catch(() => null);
        if (!stat?.isFile()) throw new Error(`File not found: ${args.path}`);
        const result = await runExtract(args as ExtractRequest);
        const counts = countsOf(result);
        if (args.outFile) {
          await fs.mkdir(path.dirname(args.outFile), { recursive: true });
          await fs.writeFile(args.outFile, JSON.stringify(result, null, 1), "utf8");
          const summary = { outFile: args.outFile, counts, calibration: result.calibration, box: result.box, elapsedMs: result.elapsedMs };
          return { content: [{ type: "text" as const, text: JSON.stringify(summary, null, 2) }] };
        }
        const text = JSON.stringify(result);
        if (text.length > INLINE_LIMIT) {
          const summary = {
            note: `Result is ${text.length} characters; pass outFile (absolute .json path) to get it, or narrow box/extract.`,
            counts,
            calibration: result.calibration,
            box: result.box,
          };
          return { content: [{ type: "text" as const, text: JSON.stringify(summary, null, 2) }] };
        }
        return { content: [{ type: "text" as const, text }] };
      } catch (error) {
        return errorResult("pdf_extract", error);
      }
    }
  );
}
