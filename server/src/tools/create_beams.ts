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

export const beamItemSchema = z
  .object({
    ...typeRefShape,
    start: xySchema.describe("Start point in plan (mm)"),
    end: xySchema.describe("End point in plan (mm)"),
    mid: xySchema.optional().describe("A point on the arc between start and end, for a curved beam"),
    level: levelRefSchema,
    startOffset: z.number().finite().optional().describe("Start level offset in mm (default 0)"),
    endOffset: z.number().finite().optional().describe("End level offset in mm (default startOffset)"),
    zJustification: z.enum(["top", "center", "bottom", "origin"]).optional().describe("Default top"),
    yJustification: z.enum(["left", "center", "right", "origin"]).optional().describe("Default center"),
    rotationDeg: z.number().finite().optional().describe("Cross-section rotation in degrees"),
    mark: markSchema.optional(),
    comments: z.string().max(1024).optional(),
    structuralUsage: z.enum(["girder", "joist", "horizontalBracing", "purlin", "other"]).optional(),
    disallowJoin: z.boolean().optional().describe("Disallow joins at both ends"),
  })
  .strict()
  .refine(hasTypeRef, typeRefMessage);

export function registerCreateBeamsTool(server: McpServer) {
  server.tool(
    "create_beams",
    "Place structural framing (beams, girders, joists) on a level from plan start/end points in mm, straight or arc (mid) - built for whole floors (thousands of beams in one call; pass big sets as a CSV/JSON dataFile, e.g. CSV headers typeId,start.x,start.y,end.x,end.y,level). " +
      "Type by typeId or familyName + typeName (create sizes first with create_family_type). Level by name, id or elevation (mm); start/end offsets are z-offsets from the level. Sets z/y justification (default top), cross-section rotation, mark, structural usage and can disallow end joins. " +
      "Each item reports id and warnings; failed items do not stop the others. Sent in chunks of 300 (one undo step per chunk).",
    {
      beams: z.array(beamItemSchema).max(MAX_INLINE_ITEMS).optional().describe("Beams to place"),
      ...bulkInputShape,
    },
    async (args) =>
      runFramingBulk("create_beams", "beams", beamItemSchema, {
        items: args.beams,
        dataFile: args.dataFile,
        dataFormat: args.dataFormat,
      })
  );
}
