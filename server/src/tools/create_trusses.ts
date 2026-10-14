import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { bulkInputShape } from "../utils/bulkInput.js";
import {
  MAX_INLINE_ITEMS,
  hasTypeRef,
  levelRefSchema,
  markSchema,
  runFramingBulk,
  typeRefMessage,
  typeRefShape,
  xySchema,
} from "../utils/framingSchemas.js";
import { expandPlanArray } from "../utils/memberArrays.js";
import { elementIdSchema } from "../utils/documentationSchemas.js";

const framingTypeRefSchema = z
  .object({
    typeId: elementIdSchema.optional().describe("Structural framing type id"),
    familyName: z.string().min(1).max(256).optional(),
    typeName: z.string().min(1).max(256).optional(),
  })
  .strict()
  .refine(hasTypeRef, typeRefMessage);

const trussArraySchema = z
  .object({
    count: z.number().int().min(1).max(500).optional().describe("Number of trusses including the first"),
    spacing: z.number().finite().positive().optional().describe("Distance between trusses in mm"),
    distance: z
      .number()
      .finite()
      .positive()
      .optional()
      .describe("Distance from the first to the last truss in mm (with spacing or count)"),
    direction: z
      .union([z.enum(["left", "right"]), xySchema])
      .optional()
      .describe("Plan direction of the array: 'left' (default) / 'right' of start->end, or an {x, y} vector"),
  })
  .strict()
  .describe("Parallel copies: give two of count, spacing and distance");

export const trussItemSchema = z
  .object({
    ...typeRefShape,
    start: xySchema.describe("Start of the truss line (bearing chord) in plan, mm"),
    end: xySchema.describe("End of the truss line in plan, mm"),
    level: levelRefSchema,
    startOffset: z.number().finite().optional().describe("Start level offset in mm (default 0)"),
    endOffset: z.number().finite().optional().describe("End level offset in mm (default startOffset)"),
    workPlane: z
      .enum(["vertical", "level"])
      .optional()
      .describe("Sketch plane: the vertical plane through the line, or a horizontal plane at its height. Default vertical, falling back to horizontal"),
    bearingChord: z.enum(["top", "bottom"]).optional().describe("Chord that sits on the line (Bearing Chord)"),
    bearingJustification: z.enum(["top", "center", "bottom"]).optional().describe("Bearing Vertical Justification"),
    height: z.number().finite().positive().optional().describe("Truss Height in mm (when the family allows it)"),
    rotationDeg: z.number().finite().optional().describe("Rotation Angle of the truss about its line, degrees"),
    memberTypes: z
      .object({
        topChord: framingTypeRefSchema.optional(),
        bottomChord: framingTypeRefSchema.optional(),
        verticalWeb: framingTypeRefSchema.optional(),
        diagonalWeb: framingTypeRefSchema.optional(),
      })
      .strict()
      .optional()
      .describe("Structural framing types for chords and webs ({typeId} or {familyName, typeName})"),
    memberTypeScope: z
      .enum(["instance", "type"])
      .optional()
      .describe("'instance' (default) retypes this truss's members; 'type' sets the truss type's chord/web parameters (all trusses of the type)"),
    mark: markSchema.optional().describe("Mark; with array, 'T1' counts T1, T2, ... and other text gets -1, -2, ..."),
    comments: z.string().max(1024).optional(),
    array: trussArraySchema.optional(),
  })
  .strict()
  .refine(hasTypeRef, typeRefMessage);

export type TrussItem = z.infer<typeof trussItemSchema>;

export function registerCreateTrussesTool(server: McpServer) {
  server.tool(
    "create_trusses",
    "Place structural trusses (Structural Trusses family types, e.g. Howe/Pratt/Warren or a custom 'Truss_12Panel') along plan lines on a level, in mm. " +
      "The line is the bearing chord; start/end offsets are z-offsets from the level (different values make a sloped truss line). Type by typeId or familyName + typeName - when no truss family is loaded, load one first with load_family (library folder 'Structural Trusses'). " +
      "Options: bearing chord top/bottom, bearing justification, truss height, rotation, chord/web framing types per truss or on the truss type, mark, comments. " +
      "'array' makes parallel copies (count/spacing/distance, perpendicular left/right or a plan vector), e.g. 16 trusses at 6000 mm. Each truss reports id, member count and warnings; results carry source (item index) and copy for arrays. Big sets via dataFile.",
    {
      trusses: z.array(trussItemSchema).max(MAX_INLINE_ITEMS).optional().describe("Trusses to place"),
      ...bulkInputShape,
    },
    async (args) =>
      runFramingBulk(
        "create_trusses",
        "trusses",
        trussItemSchema,
        { items: args.trusses, dataFile: args.dataFile, dataFormat: args.dataFormat, summary: args.summary },
        { expand: (item: TrussItem) => expandPlanArray(item) }
      )
  );
}
