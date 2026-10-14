import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { bulkInputShape } from "../utils/bulkInput.js";
import { runBulkCommand } from "../utils/bulkCommand.js";
import { levelRefSchema, markSchema, polygonSchema, typeIdSchema } from "../utils/foundationSchemas.js";
import { point2Schema } from "../utils/documentationSchemas.js";

const familyFields = {
  typeId: typeIdSchema.optional().describe("Family type id (see load_family / create_family_type)"),
  familyName: z.string().min(1).max(256).optional().describe("Loaded family name, e.g. 'M_Footing-Rectangular'"),
  typeName: z.string().min(1).max(256).optional().describe("Type of familyName (default the first type)"),
};

const isolatedSchema = z
  .object({
    kind: z.literal("isolated"),
    ...familyFields,
    x: z.number().finite().describe("X in mm"),
    y: z.number().finite().describe("Y in mm"),
    level: levelRefSchema,
    topOffset: z.number().finite().optional().describe("Top of footing relative to the level, mm (footing origin is at its top)"),
    rotationDeg: z.number().finite().optional().describe("Rotation about the vertical axis, degrees counter-clockwise"),
    mark: markSchema.optional(),
  })
  .strict();

const pileSchema = z
  .object({
    kind: z.literal("pile"),
    ...familyFields,
    x: z.number().finite().describe("X in mm"),
    y: z.number().finite().describe("Y in mm"),
    topElevation: z.number().finite().optional().describe("Absolute elevation of the pile top, mm"),
    underFoundationId: typeIdSchema.optional().describe("Pile top = underside (bounding box bottom) of this foundation element"),
    underFoundationMark: z
      .union([z.string().min(1).max(256), z.number().finite()])
      .optional()
      .describe("Pile top = underside of the structural foundation with this Mark (may be created earlier in the same call)"),
    length: z.number().positive().describe("Pile length in mm (spans [top - length, top])"),
    lengthParameter: z
      .string()
      .min(1)
      .max(64)
      .optional()
      .describe("Foundation families: parameter holding the pile length (default first of 'Pile Length', 'Depth', 'Length', 'L')"),
    rotationDeg: z.number().finite().optional().describe("Rotation about the pile axis, degrees counter-clockwise (barrettes)"),
    baseLevel: levelRefSchema.optional().describe("Level for the column constraints (default the nearest level at or below the top)"),
    mark: markSchema.optional(),
  })
  .strict();

const capSlabSchema = z
  .object({
    kind: z.literal("capSlab"),
    boundary: polygonSchema.describe("Cap outline (rectangular, hexagonal, triangular, ...) as {x,y} mm points"),
    level: levelRefSchema,
    topOffset: z.number().finite().optional().describe("Top of cap relative to the level, mm"),
    thickness: z.number().positive().optional().describe("Cap thickness mm (finds/creates 'Foundation Slab <t>mm')"),
    typeId: typeIdSchema.optional().describe("Foundation slab type id instead of thickness"),
    mark: markSchema.optional(),
  })
  .strict();

const lineSchema = z
  .object({
    kind: z.literal("line"),
    ...familyFields,
    typeName: z
      .string()
      .min(1)
      .max(256)
      .optional()
      .describe("Type of familyName; the copy is switched to it when the seed has another type (default: keep the seed's type)"),
    start: point2Schema.describe("Start point {x,y} mm"),
    end: point2Schema.describe("End point {x,y} mm"),
    level: levelRefSchema,
    seedElementId: typeIdSchema
      .optional()
      .describe("Hand-placed instance to copy (default: an existing instance of the family on the level, same type first)"),
    offset: z.number().finite().optional().describe("Offset from host (the level), mm; default the seed's offset"),
    mark: markSchema.optional(),
    comments: z.string().max(1024).optional().describe("Comments parameter"),
  })
  .strict();

export const foundationItemSchema = z
  .discriminatedUnion("kind", [isolatedSchema, pileSchema, capSlabSchema, lineSchema])
  .superRefine((item, ctx) => {
    if (item.kind === "capSlab") {
      if ((item.thickness === undefined) === (item.typeId === undefined))
        ctx.addIssue({ code: z.ZodIssueCode.custom, message: "capSlab: give exactly one of thickness or typeId" });
      return;
    }
    if (item.kind === "line") {
      if (item.typeId === undefined && item.familyName === undefined && item.seedElementId === undefined)
        ctx.addIssue({ code: z.ZodIssueCode.custom, message: "line: give typeId, familyName (+typeName) or seedElementId" });
      if (item.start.x === item.end.x && item.start.y === item.end.y)
        ctx.addIssue({ code: z.ZodIssueCode.custom, message: "line: start and end must differ" });
      return;
    }
    if (item.typeId === undefined && item.familyName === undefined)
      ctx.addIssue({ code: z.ZodIssueCode.custom, message: `${item.kind}: give typeId or familyName (+typeName)` });
    if (item.kind === "pile") {
      const tops = [item.topElevation, item.underFoundationId, item.underFoundationMark].filter((v) => v !== undefined);
      if (tops.length !== 1)
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          message: "pile: give exactly one of topElevation, underFoundationId or underFoundationMark",
        });
    }
  });

export type FoundationItem = z.infer<typeof foundationItemSchema>;

export function registerCreateFoundationsTool(server: McpServer) {
  server.tool(
    "create_foundations",
    "Create structural foundations in bulk (hundreds of piles in one call). All lengths are millimetres. kind:'isolated' places a footing / pile cap family at x,y on a level (topOffset = top of footing relative to the level, rotationDeg). " +
      "kind:'pile' places a pile spanning [top - length, top]; the top is topElevation (absolute), or the underside of a foundation given by underFoundationId or underFoundationMark (caps created earlier in the same call count). Use a Structural Foundations pile family (e.g. 'Pile-Steel Pipe'; length is an instance or type parameter Length/Depth - one type per length). Structural column families are refused under isolated footings because Revit attaches the footing to the column base and drops the cap to the pile bottom. " +
      "kind:'capSlab' makes a polygonal pile cap (hexagonal, triangular, ...) as a foundation slab with thickness or typeId. " +
      "kind:'line' makes line-based foundations (continuous grade beams / strip footings, e.g. family '_TT-Grade Beam') from start to end {x,y} on a level. Revit cannot create these reliably through the API (NewFamilyInstance leaves the geometry at the origin with no host), so each one is a copy of an instance the user placed by hand with Place on Work Plane on that level: seedElementId, else an existing instance of the family on the level (same type first). If there is none the item fails asking for one. Copies go through a floor/structural plan view of the level, then get their curve, type (typeId / typeName), offset (from host), mark and comments. " +
      "Items come from `foundations` and/or a local dataFile (JSON/JSONL/CSV; CSV headers like kind,familyName,typeName,x,y,underFoundationMark,length; for lines kind,familyName,typeName,level,start.x,start.y,end.x,end.y,mark). Put caps before their piles. Sent in chunks of 300 (one undo step and one transaction per chunk, each item isolated); each item reports id, elevations and warnings.",
    {
      foundations: z.array(foundationItemSchema).max(20000).optional().describe("Foundations to create, in order"),
      ...bulkInputShape,
    },
    async (args) =>
      runBulkCommand<FoundationItem>("create_foundations", args, "foundations", {
        itemSchema: foundationItemSchema,
        jsonFields: ["boundary"],
        chunkSize: 300,
      })
  );
}
