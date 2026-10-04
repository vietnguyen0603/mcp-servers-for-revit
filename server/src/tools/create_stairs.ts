import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { point2Schema } from "../utils/documentationSchemas.js";
import { bulkInputShape } from "../utils/bulkInput.js";
import { runBulkCommand } from "../utils/bulkCommand.js";
import { levelRefSchema, markSchema, typeIdSchema } from "../utils/foundationSchemas.js";

export const stairItemSchema = z
  .object({
    baseLevel: levelRefSchema.describe("Bottom level (name, id or elevation mm)"),
    topLevel: levelRefSchema.describe("Top level (name, id or elevation mm)"),
    baseOffset: z.number().finite().optional().describe("Base offset from baseLevel, mm (default 0)"),
    topOffset: z.number().finite().optional().describe("Top offset from topLevel, mm (default 0)"),
    start: point2Schema.describe("Start of the first run's centre line (first riser), mm"),
    direction: point2Schema.optional().describe("Walking-up direction of the first run as a plan vector {x,y}"),
    angleDeg: z.number().finite().optional().describe("Walking-up direction of the first run, degrees counter-clockwise from +X"),
    width: z.number().positive().describe("Run width, mm"),
    flights: z
      .union([z.literal(1), z.literal(2)])
      .optional()
      .describe("1 = single straight run (default); 2 = U-shaped: two parallel runs with a mid landing"),
    gap: z.number().nonnegative().optional().describe("flights 2: clear gap between the two runs, mm (default 0)"),
    side: z
      .enum(["left", "right"])
      .optional()
      .describe("flights 2: side of the first run (looking up) where the second run is placed (default right)"),
    landingDepth: z
      .number()
      .positive()
      .optional()
      .describe("flights 2: landing depth along the run direction, mm (default: an automatic landing, about the run width)"),
    riserCount: z.number().int().min(2).max(200).optional().describe("Total risers (default ceil(height / type max riser height))"),
    treadDepth: z.number().positive().optional().describe("Tread depth, mm (default the type's minimum tread depth)"),
    stairsTypeId: typeIdSchema.optional().describe("Stairs type id"),
    stairsTypeName: z.string().min(1).max(256).optional().describe("Stairs type name (default the project default stairs type)"),
    mark: markSchema.optional(),
  })
  .strict()
  .superRefine((s, ctx) => {
    if ((s.direction === undefined) === (s.angleDeg === undefined))
      ctx.addIssue({ code: z.ZodIssueCode.custom, message: "give exactly one of direction or angleDeg" });
    if (s.direction !== undefined && Math.hypot(s.direction.x, s.direction.y) < 1e-9)
      ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["direction"], message: "direction must be a non-zero vector" });
    if (s.stairsTypeId !== undefined && s.stairsTypeName !== undefined)
      ctx.addIssue({ code: z.ZodIssueCode.custom, message: "give at most one of stairsTypeId or stairsTypeName" });
    if ((s.flights ?? 1) === 1)
      for (const field of ["gap", "side", "landingDepth"] as const)
        if (s[field] !== undefined)
          ctx.addIssue({ code: z.ZodIssueCode.custom, path: [field], message: `${field} needs flights: 2` });
  });

export type StairItem = z.infer<typeof stairItemSchema>;

export function registerCreateStairsTool(server: McpServer) {
  server.tool(
    "create_stairs",
    "Create component stairs between two levels in bulk. All lengths are millimetres. Each item makes straight stairs from baseLevel(+baseOffset) to topLevel(+topOffset) starting at `start` (centre of the first riser) going up along `direction` or angleDeg, with run `width`. " +
      "flights:1 = one straight run; flights:2 = U-shaped stairs: the risers are split over two parallel runs joined by a mid landing (landingDepth, else an automatic landing), the second run placed `gap` mm beside the first on `side` (left/right). " +
      "riserCount defaults to ceil(height / max riser height of the stairs type); treadDepth defaults to the type's minimum tread depth; stairsTypeName/stairsTypeId pick the type. " +
      "Items come from `stairs` and/or a local dataFile (JSON/JSONL/CSV; CSV headers like start.x,start.y). Each stair is its own undo step; each item reports id, riser count, actual riser height, tread depth, run and landing ids and warnings.",
    {
      stairs: z.array(stairItemSchema).max(500).optional().describe("Stairs to create, in order"),
      ...bulkInputShape,
    },
    async (args) =>
      runBulkCommand<StairItem>("create_stairs", args, "stairs", {
        itemSchema: stairItemSchema,
        jsonFields: ["start", "direction"],
        chunkSize: 50,
      })
  );
}
