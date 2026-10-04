import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { bulkInputShape } from "../utils/bulkInput.js";
import { runBulkCommand } from "../utils/bulkCommand.js";
import { levelRefSchema, markSchema, polygonSchema, typeIdSchema } from "../utils/foundationSchemas.js";

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

export const foundationItemSchema = z
  .discriminatedUnion("kind", [isolatedSchema, pileSchema, capSlabSchema])
  .superRefine((item, ctx) => {
    if (item.kind === "capSlab") {
      if ((item.thickness === undefined) === (item.typeId === undefined))
        ctx.addIssue({ code: z.ZodIssueCode.custom, message: "capSlab: give exactly one of thickness or typeId" });
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
      "Items come from `foundations` and/or a local dataFile (JSON/JSONL/CSV; CSV headers like kind,familyName,typeName,x,y,underFoundationMark,length). Put caps before their piles. Sent in chunks of 300 (one undo step and one transaction per chunk, each item isolated); each item reports id, elevations and warnings.",
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
