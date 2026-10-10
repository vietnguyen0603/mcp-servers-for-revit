import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { levelRefSchema, markSchema, typeRefShape } from "../utils/framingSchemas.js";
import { structuralMemberFilterSchema } from "./summarize_structural_members.js";

const changesSchema = z
  .object({
    ...typeRefShape,
    structuralUsage: z
      .enum(["girder", "joist", "horizontalBracing", "purlin", "other"])
      .optional()
      .describe("Framing only (headers/lintels are usually 'other')"),
    referenceLevel: levelRefSchema.optional().describe("Framing: new reference level"),
    startOffset: z.number().finite().optional().describe("Framing: start level offset in mm"),
    endOffset: z.number().finite().optional().describe("Framing: end level offset in mm (default startOffset when startOffset is given)"),
    zJustification: z.enum(["top", "center", "bottom", "origin"]).optional().describe("Framing"),
    yJustification: z.enum(["left", "center", "right", "origin"]).optional().describe("Framing"),
    baseLevel: levelRefSchema.optional().describe("Columns: base level"),
    baseOffset: z.number().finite().optional().describe("Columns: base offset in mm"),
    topLevel: levelRefSchema.optional().describe("Columns: top level"),
    topOffset: z.number().finite().optional().describe("Columns: top offset in mm"),
    mark: markSchema.optional(),
    comments: z.string().max(1024).optional(),
  })
  .strict()
  .refine((s) => Object.values(s).some((v) => v !== undefined), { message: "Give at least one change" })
  .refine((s) => s.typeName === undefined || s.familyName !== undefined, { message: "typeName needs familyName" });

export function registerModifyStructuralMembersTool(server: McpServer) {
  server.tool(
    "modify_structural_members",
    "Edit structural framing and columns that are already placed, selected by filter (elementIds, category, comments, marks, familyName/typeName, level), in one undo step: swap the type (typeId or familyName + typeName, same category), set structural usage (girder/joist/purlin/horizontalBracing/other), " +
      "beam start/end offsets, z/y justification and reference level, column base/top level and offsets (mm), and mark/comments. " +
      "Changes that do not apply to a member's category (e.g. topOffset on a beam, structuralUsage on a column) are reported as skipped, so framing and columns can share one call. " +
      "Use dryRun: true to list the matched members first; summarize_structural_members checks the result. Each member reports what changed and any warnings; failed members do not stop the others.",
    {
      filter: structuralMemberFilterSchema,
      set: changesSchema.describe("Changes to apply to every matched member"),
      dryRun: z.boolean().default(false).describe("Only list the matched members"),
      maxElements: z.number().int().min(1).max(50000).default(5000).describe("Refuse to run when more members match"),
    },
    async (args) => {
      if (!Object.values(args.filter).some((v) => v !== undefined)) {
        return { content: [{ type: "text" as const, text: "Give at least one filter criterion (use category: 'all' to edit every member)." }], isError: true };
      }
      return sendDocumentationCommand("modify_structural_members", args, 300000);
    }
  );
}
