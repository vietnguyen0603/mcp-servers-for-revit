import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { point2Schema } from "../utils/documentationSchemas.js";
import { bulkInputShape } from "../utils/bulkInput.js";
import { runBulkCommand } from "../utils/bulkCommand.js";
import { levelRefSchema, markSchema, polygonSchema, segmentSchema, typeIdSchema } from "../utils/foundationSchemas.js";

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
    mark: markSchema.optional(),
  })
  .strict()
  .refine((s) => (s.boundary !== undefined) !== (s.boundarySegments !== undefined), {
    message: "Give exactly one of boundary or boundarySegments",
  })
  .refine((s) => [s.typeId, s.typeName, s.thickness].filter((v) => v !== undefined).length <= 1, {
    message: "Give at most one of typeId, typeName or thickness",
  });

export type SlabItem = z.infer<typeof slabItemSchema>;

export function registerCreateSlabsTool(server: McpServer) {
  server.tool(
    "create_slabs",
    "Create floors / structural slabs and foundation slabs from polygons, with openings (inner loops), arcs, per-slab level offsets (step zones such as -50/-100/-1100 mm), a slope arrow and a mark. All lengths are millimetres. " +
      "Type: typeId, typeName, or thickness (finds/creates 'Slab <t>mm'), else the default type. foundation:true makes a structural foundation slab (basement slab, raft). " +
      "Items come from `slabs` and/or a local dataFile (JSON/JSONL/CSV; in CSV put boundary/openings as JSON text). Sent in chunks of 100, one undo step per chunk; each item reports id, type, thickness, areaM2 and warnings.",
    {
      slabs: z.array(slabItemSchema).max(5000).optional().describe("Slabs to create"),
      ...bulkInputShape,
    },
    async (args) =>
      runBulkCommand<SlabItem>("create_slabs", args, "slabs", {
        itemSchema: slabItemSchema,
        jsonFields: ["boundary", "boundarySegments", "openings", "slopeArrow"],
        chunkSize: 100,
      })
  );
}
