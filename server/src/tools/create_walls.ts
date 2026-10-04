import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { point2Schema } from "../utils/documentationSchemas.js";
import { bulkInputShape } from "../utils/bulkInput.js";
import { runBulkCommand } from "../utils/bulkCommand.js";
import { levelRefSchema, markSchema, typeIdSchema } from "../utils/foundationSchemas.js";

export const wallLocationLines = [
  "wallCenterline",
  "coreCenterline",
  "finishFaceExterior",
  "finishFaceInterior",
  "coreExterior",
  "coreInterior",
] as const;

export const wallItemSchema = z
  .object({
    start: point2Schema.describe("Start of the wall location line in plan (mm)"),
    end: point2Schema.describe("End of the wall location line in plan (mm)"),
    mid: point2Schema.optional().describe("A point on the arc between start and end, for a curved wall"),
    baseLevel: levelRefSchema,
    baseOffset: z
      .number()
      .finite()
      .optional()
      .describe("Base offset from baseLevel in mm (default 0; e.g. -37500 for a diaphragm wall toe below the level)"),
    topLevel: levelRefSchema.optional().describe("Top constraint level (name, id or elevation mm); omit for an unconnected height"),
    topOffset: z.number().finite().optional().describe("Top offset from topLevel in mm (default 0)"),
    height: z.number().positive().optional().describe("Unconnected height in mm, when there is no topLevel"),
    thickness: z
      .number()
      .positive()
      .optional()
      .describe("Wall thickness in mm: finds or creates the basic wall type '<typeNamePrefix> <t>mm' by duplicating a single-layer (concrete preferred) basic wall type"),
    typeId: typeIdSchema.optional().describe("Wall type id"),
    typeName: z.string().min(1).max(256).optional().describe("Wall type name (exact, case-insensitive)"),
    structural: z.boolean().optional().describe("Structural wall (default true)"),
    locationLine: z
      .enum(wallLocationLines)
      .optional()
      .describe("Which line of the wall start/end describe (default wallCenterline)"),
    flip: z.boolean().optional().describe("Flip the wall (exterior side to the other side of the line)"),
    mark: markSchema.optional(),
    comments: z.string().max(1024).optional(),
  })
  .strict()
  .refine((w) => [w.typeId, w.typeName, w.thickness].filter((v) => v !== undefined).length <= 1, {
    message: "Give at most one of typeId, typeName or thickness",
  })
  .refine((w) => (w.topLevel !== undefined) !== (w.height !== undefined), {
    message: "Give either topLevel (+ topOffset) or height",
  })
  .refine((w) => w.topOffset === undefined || w.topLevel !== undefined, {
    message: "topOffset needs topLevel (use height for an unconnected wall)",
  });

export type WallItem = z.infer<typeof wallItemSchema>;

export function registerCreateWallsTool(server: McpServer) {
  server.tool(
    "create_walls",
    "Create walls (structural by default: core walls, shear walls, retaining and diaphragm walls; or architectural) from plan start/end points in mm, straight or arc (mid). " +
      "Base: baseLevel (name, id or elevation mm) + baseOffset, so the bottom can sit anywhere (e.g. a diaphragm wall with baseOffset -37500). Top: topLevel + topOffset, or an unconnected height. " +
      "Type: typeId, typeName, or thickness (finds/creates the basic wall type '<typeNamePrefix> <t>mm', default prefix 'Wall'), else the default wall type. locationLine says which line start/end describe (default wallCenterline); flip, mark and comments are optional. " +
      "Items come from `walls` and/or a local dataFile (JSON/JSONL/CSV, e.g. CSV headers start.x,start.y,end.x,end.y,baseLevel,topLevel,thickness). Sent in chunks of 300, one undo step per chunk; each item reports id, type, thickness, base/top elevations, height, length and warnings.",
    {
      walls: z.array(wallItemSchema).max(5000).optional().describe("Walls to create"),
      typeNamePrefix: z
        .string()
        .min(1)
        .max(128)
        .optional()
        .describe("Prefix of the wall type names made for thickness (default 'Wall' -> 'Wall 300mm')"),
      ...bulkInputShape,
    },
    async (args) =>
      runBulkCommand<WallItem>("create_walls", args, "walls", {
        itemSchema: wallItemSchema,
        jsonFields: ["start", "end", "mid"],
        stringFields: ["typeName", "comments"],
        base: args.typeNamePrefix ? { typeNamePrefix: args.typeNamePrefix } : {},
        chunkSize: 300,
      })
  );
}
