import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { point2Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

const gridName = z.string().min(1).max(64).describe("Grid name exactly as shown in the bubble, e.g. '1', 'C.5', 'E.8'");

const lineGrid = z
  .object({
    name: gridName,
    start: point2Schema.describe("Start point (mm)"),
    end: point2Schema.describe("End point (mm)"),
  })
  .strict()
  .refine((g) => Math.hypot(g.end.x - g.start.x, g.end.y - g.start.y) >= 1, {
    message: "start and end must be at least 1 mm apart",
  });

const arcGrid = z
  .object({
    name: gridName,
    center: point2Schema.describe("Arc centre (mm)"),
    radius: z.number().positive().finite().describe("Arc radius (mm)"),
    startAngleDeg: z.number().finite().describe("Start angle in degrees, counter-clockwise from +X"),
    endAngleDeg: z.number().finite().describe("End angle in degrees (> startAngleDeg)"),
  })
  .strict()
  .refine((g) => g.endAngleDeg > g.startAngleDeg && g.endAngleDeg - g.startAngleDeg < 360, {
    message: "endAngleDeg must be greater than startAngleDeg and the sweep below 360 degrees",
  });

export const gridItemSchema = z.union([lineGrid, arcGrid]);

export const gridAxisSchema = z
  .object({
    direction: z
      .enum(["x", "y"])
      .describe("'x': vertical grid lines placed at X positions; 'y': horizontal grid lines placed at Y positions"),
    labels: z.array(z.string().min(1).max(64)).min(1).max(200).describe("Grid names, one per position, e.g. ['A','B','C','C.5']"),
    positions: z
      .array(z.number().finite())
      .min(1)
      .max(200)
      .describe("Positions in mm along the axis (X for direction 'x', Y for direction 'y'), e.g. [0, 10000, 20000, 30000, 42500]"),
    from: z.number().finite().describe("Line start along the other axis (mm)"),
    to: z.number().finite().describe("Line end along the other axis (mm)"),
  })
  .strict()
  .refine((a) => a.labels.length === a.positions.length, { message: "labels and positions must have the same length" })
  .refine((a) => Math.abs(a.to - a.from) >= 1, { message: "from and to must be at least 1 mm apart" });

type GridItem = z.infer<typeof gridItemSchema>;
type GridAxis = z.infer<typeof gridAxisSchema>;

/** Expands the axes convenience form into explicit straight grids. */
export function expandGridAxes(axes: GridAxis[]): GridItem[] {
  return axes.flatMap((axis) =>
    axis.positions.map((position, index) =>
      axis.direction === "x"
        ? { name: axis.labels[index], start: { x: position, y: axis.from }, end: { x: position, y: axis.to } }
        : { name: axis.labels[index], start: { x: axis.from, y: position }, end: { x: axis.to, y: position } }
    )
  );
}

export function registerCreateGridsTool(server: McpServer) {
  server.tool(
    "create_grids",
    "Create named structural/architectural grids from explicit lines or arcs, or from an axes table with irregular spacing (e.g. axes:[{direction:'x', labels:['1','2','3'], positions:[0,10000,20000], from:-5000, to:98000}, {direction:'y', labels:['A','B','C','C.5'], positions:[...], from:-5000, to:153000}]). " +
      "All coordinates are millimetres. Names are used exactly as given (C.5, E.8, ...). " +
      "ifExists: 'skip' (default) keeps an existing grid with the same name, 'error' fails that item, 'replace' deletes and recreates it (this deletes dimensions and tags that referenced the old grid). " +
      "Each item reports id, name and created/skipped/replaced. One undo step.",
    {
      grids: z.array(gridItemSchema).max(500).optional().describe("Explicit grids: { name, start, end } or { name, center, radius, startAngleDeg, endAngleDeg }"),
      axes: z.array(gridAxisSchema).max(20).optional().describe("Convenience form: rows of labels/positions expanded into straight grids"),
      ifExists: z.enum(["skip", "error", "replace"]).optional().describe("When a grid with the same name exists (default skip)"),
      gridTypeName: z.string().min(1).max(256).optional().describe("Grid type name to assign (default: the document's default grid type)"),
    },
    async (args) => {
      const grids = [...(args.grids ?? []), ...expandGridAxes(args.axes ?? [])];
      if (grids.length === 0) {
        return {
          content: [{ type: "text" as const, text: "create_grids failed: give 'grids' or 'axes'" }],
          isError: true,
        };
      }
      if (grids.length > 1000) {
        return {
          content: [{ type: "text" as const, text: "create_grids failed: at most 1000 grids per call" }],
          isError: true,
        };
      }
      const params: Record<string, unknown> = { grids };
      if (args.ifExists) params.ifExists = args.ifExists;
      if (args.gridTypeName) params.gridTypeName = args.gridTypeName;
      return sendDocumentationCommand("create_grids", params);
    }
  );
}
