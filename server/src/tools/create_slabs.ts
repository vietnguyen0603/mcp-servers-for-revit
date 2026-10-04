import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { point2Schema } from "../utils/documentationSchemas.js";
import { bulkInputShape } from "../utils/bulkInput.js";
import { runBulkCommand } from "../utils/bulkCommand.js";
import { levelRefSchema, markSchema, polygonSchema, segmentSchema, typeIdSchema } from "../utils/foundationSchemas.js";

const slabTypeCount = (s: { typeId?: number; typeName?: string; thickness?: number }) =>
  [s.typeId, s.typeName, s.thickness].filter((v) => v !== undefined).length;

export const slabZoneSchema = z
  .object({
    boundary: polygonSchema.describe("Zone outline, strictly inside the slab boundary, not touching openings or other zones"),
    thickness: z.number().positive().optional().describe("Zone thickness mm (finds/creates 'Slab <t>mm'), e.g. 450 for a PT band in a 250 slab"),
    typeId: typeIdSchema.optional().describe("Zone floor type id"),
    typeName: z.string().min(1).max(256).optional().describe("Zone floor type name"),
    offset: z
      .number()
      .finite()
      .optional()
      .describe("Top of the zone relative to the slab level, mm (default the slab offset; e.g. -50 for a recess)"),
    mark: markSchema.optional(),
  })
  .strict()
  .refine((zone) => slabTypeCount(zone) === 1, { message: "zone: give exactly one of thickness, typeId or typeName" });

export const dropPanelSchema = z
  .object({
    boundary: polygonSchema.describe("Drop panel outline (inside the slab boundary)"),
    depth: z.number().positive().describe("Depth below the slab soffit, mm (the drop panel slab thickness)"),
    typeId: typeIdSchema.optional().describe("Floor type id (default: finds/creates 'Slab <depth>mm')"),
    typeName: z.string().min(1).max(256).optional().describe("Floor type name (default: finds/creates 'Slab <depth>mm')"),
    mark: markSchema.optional(),
  })
  .strict()
  .refine((d) => d.typeId === undefined || d.typeName === undefined, {
    message: "dropPanel: give at most one of typeId or typeName",
  });

export const slabItemSchema = z
  .object({
    level: levelRefSchema,
    offset: z.number().finite().optional().describe("Top of slab relative to the level, mm (e.g. -50, -1100 for step zones)"),
    boundary: polygonSchema.optional().describe("Outer boundary as a closed polygon of {x,y} mm points"),
    boundarySegments: z
      .array(segmentSchema)
      .min(2)
      .max(2000)
      .optional()
      .describe("Alternative outer boundary as consecutive {start,end,mid?} segments (mid = point on an arc)"),
    openings: z.array(polygonSchema).max(500).optional().describe("Openings (shafts, voids) as closed polygons inside the boundary"),
    typeId: typeIdSchema.optional().describe("Floor / foundation slab type id"),
    typeName: z.string().min(1).max(256).optional().describe("Floor / foundation slab type name"),
    thickness: z
      .number()
      .positive()
      .optional()
      .describe("Thickness in mm: finds or creates the type 'Slab <t>mm' ('Foundation Slab <t>mm' when foundation) by duplicating a single-layer type"),
    structural: z.boolean().optional().describe("Structural floor (default true)"),
    foundation: z
      .boolean()
      .optional()
      .describe("Create a structural foundation slab (foundation slab type, category Structural Foundations), e.g. a basement slab or raft"),
    slopeArrow: z
      .object({
        start: point2Schema.describe("Tail of the arrow (stays at the slab height)"),
        end: point2Schema.describe("Head of the arrow"),
        riseMm: z.number().finite().describe("Rise from tail to head in mm (negative slopes down)"),
      })
      .strict()
      .optional(),
    zones: z
      .array(slabZoneSchema)
      .max(200)
      .optional()
      .describe(
        "Zones with their own thickness/type/offset (PT bands, thickenings, recesses): each zone is cut out of this slab and created as a separate slab"
      ),
    dropPanels: z
      .array(dropPanelSchema)
      .max(500)
      .optional()
      .describe("Drop panels: slabs of thickness = depth hung under this slab (top at the slab soffit, or the soffit of the zone containing it)"),
    mark: markSchema.optional(),
  })
  .strict()
  .refine((s) => (s.boundary !== undefined) !== (s.boundarySegments !== undefined), {
    message: "Give exactly one of boundary or boundarySegments",
  })
  .refine((s) => slabTypeCount(s) <= 1, {
    message: "Give at most one of typeId, typeName or thickness",
  });

export type SlabItem = z.infer<typeof slabItemSchema>;

export function registerCreateSlabsTool(server: McpServer) {
  server.tool(
    "create_slabs",
    "Create floors / structural slabs and foundation slabs from polygons, with openings (inner loops), arcs, per-slab level offsets (step zones such as -50/-100/-1100 mm), a slope arrow and a mark. All lengths are millimetres. " +
      "zones:[{boundary, thickness|typeId|typeName, offset?}] cut each zone out of the slab and create it as its own slab (PT band zones, thickened or recessed areas); invalid zones (outside the boundary or overlapping openings/zones) are skipped with a warning. " +
      "dropPanels:[{boundary, depth}] add slabs of thickness = depth hung under the slab soffit (drop panels / thickenings). " +
      "Type: typeId, typeName, or thickness (finds/creates 'Slab <t>mm'), else the default type. foundation:true makes a structural foundation slab (basement slab, raft). " +
      "Items come from `slabs` and/or a local dataFile (JSON/JSONL/CSV; in CSV put boundary/openings as JSON text). Sent in chunks of 100, one undo step per chunk; each item reports id, type, thickness, areaM2, zone/drop panel ids and warnings.",
    {
      slabs: z.array(slabItemSchema).max(5000).optional().describe("Slabs to create"),
      ...bulkInputShape,
    },
    async (args) =>
      runBulkCommand<SlabItem>("create_slabs", args, "slabs", {
        itemSchema: slabItemSchema,
        jsonFields: ["boundary", "boundarySegments", "openings", "slopeArrow", "zones", "dropPanels"],
        chunkSize: 100,
      })
  );
}
