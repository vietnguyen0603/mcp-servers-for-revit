import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point3Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

const lineStyle = z.string().min(1).max(256);

const lineItemSchema = z.union([
  z
    .object({ start: point3Schema, end: point3Schema, lineStyle: lineStyle.optional() })
    .strict()
    .describe("Straight line"),
  z
    .object({
      center: point3Schema,
      radius: z.number().positive().describe("Radius in mm"),
      startAngleDeg: z.number().finite().optional().default(0),
      endAngleDeg: z.number().finite().optional().default(360),
      lineStyle: lineStyle.optional(),
    })
    .strict()
    .refine((arc) => arc.endAngleDeg > arc.startAngleDeg, { message: "endAngleDeg must exceed startAngleDeg" })
    .describe("Arc; angles are measured counter-clockwise from the view's right direction. 0–360 draws a full circle"),
  z
    .object({
      points: z.array(point3Schema).min(2).max(1000),
      closed: z.boolean().optional().default(false),
      lineStyle: lineStyle.optional(),
    })
    .strict()
    .describe("Polyline through the points"),
]);

export function registerCreateDetailLinesTool(server: McpServer) {
  server.tool(
    "create_detail_lines",
    "Create view-specific detail lines, arcs and polylines in a view or on a sheet. Points are millimetres (model coordinates in model views, sheet coordinates on sheets and drafting views) and are projected onto the view plane. lineStyle is the name of a Lines subcategory such as 'Thin Lines'; an item's lineStyle overrides the request default. Not available in 3D views or schedules.",
    {
      viewId: elementIdSchema.optional().describe("View or sheet (default active view)"),
      lineStyle: lineStyle.optional().describe("Default line style for all items"),
      lines: z.array(lineItemSchema).min(1).max(1000),
    },
    async (args) => sendDocumentationCommand("create_detail_lines", args)
  );
}
