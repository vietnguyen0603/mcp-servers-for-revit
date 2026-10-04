import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { point2Schema } from "../utils/documentationSchemas.js";
import { bulkInputShape } from "../utils/bulkInput.js";
import { runBulkCommand } from "../utils/bulkCommand.js";
import { levelRefSchema, markSchema, polygonSchema, typeIdSchema } from "../utils/foundationSchemas.js";

const shaftSchema = z
  .object({
    kind: z.literal("shaft"),
    boundary: polygonSchema.describe("Shaft outline as a closed polygon of {x,y} mm points"),
    baseLevel: levelRefSchema.describe("Bottom level of the shaft (name, id or elevation mm)"),
    baseOffset: z.number().finite().optional().describe("Bottom of the shaft relative to baseLevel, mm (default 0)"),
    topLevel: levelRefSchema.describe("Top level of the shaft (name, id or elevation mm)"),
    topOffset: z.number().finite().optional().describe("Top of the shaft relative to topLevel, mm (default 0)"),
    mark: markSchema.optional(),
  })
  .strict();

const wallOpeningSchema = z
  .object({
    kind: z.literal("wall"),
    wallId: typeIdSchema.optional().describe("Host wall id; else the wall nearest `point` on `level`"),
    point: point2Schema
      .optional()
      .describe("Opening centre in plan, mm (projected onto the wall line; default the wall midpoint when wallId is given)"),
    level: levelRefSchema
      .optional()
      .describe("Level to search the host wall on (required without wallId); sillHeight is then measured from this level"),
    searchRadius: z.number().positive().optional().describe("Max plan distance from point to the wall line, mm (default 1000)"),
    width: z.number().positive().describe("Opening width along the wall, mm"),
    height: z.number().positive().describe("Opening height, mm"),
    sillHeight: z
      .number()
      .finite()
      .optional()
      .describe("Bottom of the opening above `level` (when given) or above the wall base, mm (default 0)"),
    mark: markSchema.optional(),
  })
  .strict();

const floorOpeningSchema = z
  .object({
    kind: z.literal("floor"),
    boundary: polygonSchema.describe("Opening outline as a closed polygon of {x,y} mm points"),
    floorId: typeIdSchema.optional().describe("Host floor / slab id"),
    level: levelRefSchema.optional().describe("Find the host floor on this level (instead of floorId)"),
    point: point2Schema
      .optional()
      .describe("With level: a plan point inside the host floor, mm (default the centroid of boundary)"),
    perpendicular: z
      .boolean()
      .optional()
      .describe("Cut perpendicular to the floor face (default true); false cuts vertically (matters for sloped slabs)"),
    mark: markSchema.optional(),
  })
  .strict();

export const openingItemSchema = z
  .discriminatedUnion("kind", [shaftSchema, wallOpeningSchema, floorOpeningSchema])
  .superRefine((item, ctx) => {
    if (item.kind === "wall" && item.wallId === undefined && (item.point === undefined || item.level === undefined))
      ctx.addIssue({ code: z.ZodIssueCode.custom, message: "wall: give wallId, or point and level to find the host wall" });
    if (item.kind === "floor") {
      if ((item.floorId === undefined) === (item.level === undefined))
        ctx.addIssue({ code: z.ZodIssueCode.custom, message: "floor: give exactly one of floorId or level" });
      if (item.floorId !== undefined && item.point !== undefined)
        ctx.addIssue({ code: z.ZodIssueCode.custom, message: "floor: point is only used with level" });
    }
  });

export type OpeningItem = z.infer<typeof openingItemSchema>;

export function registerCreateOpeningsTool(server: McpServer) {
  server.tool(
    "create_openings",
    "Create openings in bulk. All lengths are millimetres. kind:'shaft' makes one vertical shaft opening from a polygon boundary between baseLevel(+baseOffset) and topLevel(+topOffset) that cuts every floor, roof and ceiling it passes (use one shaft per core/lift/riser instead of per-slab openings). " +
      "kind:'wall' cuts a rectangular opening (width x height, bottom at sillHeight) in a straight wall given by wallId, or found as the wall nearest `point` on `level` (sill measured from that level, else from the wall base); the opening is centred at `point` along the wall. " +
      "kind:'floor' cuts a polygon opening in one floor/slab given by floorId, or found on `level` at `point` (default the boundary centroid). " +
      "Items come from `openings` and/or a local dataFile (JSON/JSONL/CSV; in CSV put boundary/point as JSON text). Sent in chunks of 200 (one undo step per chunk, each item isolated); each item reports id, hostId and warnings.",
    {
      openings: z.array(openingItemSchema).max(5000).optional().describe("Openings to create, in order"),
      ...bulkInputShape,
    },
    async (args) =>
      runBulkCommand<OpeningItem>("create_openings", args, "openings", {
        itemSchema: openingItemSchema,
        jsonFields: ["boundary", "point"],
        chunkSize: 200,
      })
  );
}
