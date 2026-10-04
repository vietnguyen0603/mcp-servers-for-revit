import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";
import {
  MODEL_QA_TIMEOUT_MS,
  categoryNameSchema,
  levelNamesSchema,
  modelElementIdsSchema,
} from "../utils/modelQaSchemas.js";

export const joinPairSchema = z
  .object({
    cut: categoryNameSchema.describe("Category whose elements get cut, e.g. 'StructuralFraming'"),
    by: categoryNameSchema.describe("Category whose elements do the cutting, e.g. 'StructuralColumns'"),
    cutType: z
      .string()
      .min(1)
      .max(256)
      .optional()
      .describe("Only cut elements whose type name contains this text (case-insensitive), e.g. 'Foundation Slab 1000mm'"),
    byType: z
      .string()
      .min(1)
      .max(256)
      .optional()
      .describe("Only cutting elements whose type name contains this text. With cutType on a same-category pair the cut order is enforced too (e.g. a 3000 mm pile cap cuts the 1000 mm raft)"),
  })
  .strict();

export const joinElementsShape = {
  pairs: z
    .array(joinPairSchema)
    .min(1)
    .max(20)
    .describe("Category pairs: elements of `by` cut intersecting elements of `cut`"),
  levels: levelNamesSchema
    .optional()
    .describe("Only pairs where at least one element is on one of these levels (level names)"),
  elementIds: modelElementIdsSchema.optional().describe("Only pairs that involve at least one of these elements"),
  mode: z
    .enum(["join", "unjoin", "switchOrderOnly"])
    .optional()
    .default("join")
    .describe(
      "join (default): join intersecting pairs and make `by` the cutting element; unjoin: unjoin joined pairs; switchOrderOnly: only fix the cut order of pairs that are already joined"
    ),
  batchSize: z
    .number()
    .int()
    .min(50)
    .max(5000)
    .optional()
    .default(500)
    .describe("Join operations per transaction (each transaction is one undo step)"),
  maxErrors: z.number().int().min(0).max(500).optional().default(20).describe("Errors listed in the report"),
};

export function registerJoinElementsTool(server: McpServer) {
  server.tool(
    "join_elements",
    "Bulk Join Geometry between model categories, like the Join Geometry + Switch Join Order workflow of concrete modellers. " +
      "`pairs: [{ cut: 'StructuralFraming', by: 'StructuralColumns' }]` means columns cut the beams they intersect. Categories: Walls, StructuralColumns, StructuralFraming, Floors, StructuralFoundation (or OST_ names). " +
      "Typical concrete hierarchy: columns cut beams and slabs; walls cut beams; floors cut beams; caps (StructuralFoundation) cut piles - pairs [{cut:'StructuralFraming',by:'StructuralColumns'},{cut:'Floors',by:'StructuralColumns'},{cut:'StructuralFraming',by:'Walls'},{cut:'StructuralFraming',by:'Floors'}]. " +
      "Candidates come from a bounding-box sweep, then a solid intersection test (pairs that only touch are not joined). mode 'join' joins and switches the order so `by` cuts; 'switchOrderOnly' only fixes already-joined pairs; 'unjoin' removes joins. " +
      "Scope with levels (either element on the level) or elementIds. Runs in transactions of batchSize joins; 'joined but do not intersect' warnings are deleted. " +
      "Reports per pair and in total: candidates tested, joined, switched, alreadyOk, unjoined, failed (+ first errors). Modifies the model; can take minutes on large models.",
    joinElementsShape,
    async (args) => sendDocumentationCommand("join_elements", args, MODEL_QA_TIMEOUT_MS)
  );
}
