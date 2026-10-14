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
import { arrayMarks, generateMembers } from "../utils/memberArrays.js";

const xyzSchema = z.object({
  x: z.number().finite().describe("X in mm"),
  y: z.number().finite().describe("Y in mm"),
  z: z.number().finite().optional().describe("Height above the level in mm (default 0)"),
});

const lineSchema = z
  .object({ start: xyzSchema, end: xyzSchema })
  .strict();

export const memberArrayItemSchema = z
  .object({
    ...typeRefShape,
    level: levelRefSchema.describe("Level the members are hosted on; every z is measured from it"),
    baseLine: lineSchema.describe("First bounding line, e.g. the eave; members run parallel to it"),
    farLine: lineSchema
      .optional()
      .describe("Second bounding line, e.g. the ridge; spacing is measured from baseLine towards it, perpendicular to baseLine in 3D (along the slope)"),
    direction: xyzSchema.optional().describe("Instead of farLine: layout direction vector (x, y, z)"),
    distance: z.number().finite().positive().optional().describe("With direction: layout depth in mm along it"),
    spacing: z.number().finite().positive().optional().describe("Fixed spacing in mm from the first member; remainder at the far end"),
    count: z.number().int().min(1).max(2000).optional().describe("Fixed number of members, evenly spaced from first to last position"),
    maxSpacing: z.number().finite().positive().optional().describe("Fewest evenly spaced members with spacing at most this (mm), both ends included"),
    firstOffset: z.number().finite().min(0).optional().describe("Distance of the first member from baseLine in mm (default 0)"),
    lastOffset: z.number().finite().min(0).optional().describe("Distance of the last position from the far line / distance end in mm (default 0)"),
    includeEnd: z.boolean().optional().describe("With spacing: add a member at the far end when the spacing does not land on it"),
    boundary: z
      .array(xySchema)
      .min(3)
      .max(500)
      .optional()
      .describe("Plan polygon (mm) to trim the members to; parts outside are dropped, concave shapes may split a member"),
    minLength: z.number().finite().positive().optional().describe("Drop trimmed pieces shorter than this in mm (default 10)"),
    zJustification: z.enum(["top", "center", "bottom", "origin"]).optional().describe("Default top"),
    yJustification: z.enum(["left", "center", "right", "origin"]).optional().describe("Default center"),
    followSlope: z.boolean().optional().describe("Rotate the cross-section to the slope of the layout plane"),
    rotationDeg: z.number().finite().optional().describe("Cross-section rotation in degrees (added to the slope rotation)"),
    structuralUsage: z.enum(["girder", "joist", "horizontalBracing", "purlin", "other"]).optional().describe("Default purlin"),
    mark: markSchema.optional().describe("'P1' counts P1, P2, ...; other text gets -1, -2, ..."),
    comments: z.string().max(1024).optional(),
    disallowJoin: z.boolean().optional().describe("Disallow joins at both ends"),
  })
  .strict()
  .refine(hasTypeRef, typeRefMessage)
  .refine((item) => item.farLine !== undefined || (item.direction !== undefined && item.distance !== undefined), {
    message: "Give farLine, or direction + distance",
  })
  .refine((item) => [item.spacing, item.count, item.maxSpacing].filter((v) => v !== undefined).length === 1, {
    message: "Give exactly one of spacing, count or maxSpacing",
  });

export type MemberArrayItem = z.infer<typeof memberArrayItemSchema>;

/** create_beams items for one member array. */
export function memberArrayBeams(item: MemberArrayItem): Record<string, unknown>[] {
  const members = generateMembers(item, item.boundary, item.minLength ?? 10);
  const marks = arrayMarks(item.mark, members.length);
  return members.map((member, i) => {
    const rotation = (item.followSlope ? member.slopeRotationDeg : 0) + (item.rotationDeg ?? 0);
    const beam: Record<string, unknown> = {
      start: member.start,
      end: member.end,
      level: item.level,
      startOffset: member.startOffset,
      endOffset: member.endOffset,
      structuralUsage: item.structuralUsage ?? "purlin",
    };
    for (const key of ["typeId", "familyName", "typeName", "zJustification", "yJustification", "comments", "disallowJoin"] as const) {
      if (item[key] !== undefined) beam[key] = item[key];
    }
    if (Math.abs(rotation) > 1e-9) beam.rotationDeg = Math.round(rotation * 1000) / 1000;
    if (marks[i] !== undefined) beam.mark = marks[i];
    return beam;
  });
}

export function registerCreateMemberArrayTool(server: McpServer) {
  server.tool(
    "create_member_array",
    "Lay out parallel framing members - purlins on a sloped roof, girts on a wall, joists - between two bounding lines and place them as structural framing (same Revit command as create_beams). " +
      "Give baseLine (e.g. eave) and farLine (e.g. ridge) as 3D points in mm with z measured from the level, or baseLine + direction + distance; then spacing, count or maxSpacing, measured along the slope (perpendicular to baseLine in 3D), with optional first/last offsets. " +
      "Optional plan boundary polygon trims the members; followSlope rotates the cross-section into the roof plane. Structural usage defaults to purlin; type by typeId or familyName + typeName; z/y justification, mark (counted), comments, disallowJoin. " +
      "Returns the bulk summary of the created beams; each result carries source (array index) and copy (member number).",
    {
      arrays: z.array(memberArrayItemSchema).max(MAX_INLINE_ITEMS).optional().describe("Member layouts"),
      ...bulkInputShape,
    },
    async (args) =>
      runFramingBulk(
        "create_member_array",
        "arrays",
        memberArrayItemSchema,
        { items: args.arrays, dataFile: args.dataFile, dataFormat: args.dataFormat, summary: args.summary },
        { expand: memberArrayBeams, sendCommand: "create_beams", sendKey: "beams" }
      )
  );
}
