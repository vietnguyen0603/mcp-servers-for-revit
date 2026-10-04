import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

const familyRef = z.union([
  z.string().min(1).max(256),
  z
    .object({
      name: z.string().min(1).max(256).describe("Family name; wildcards * and ? allowed, e.g. 'M_Pile*'"),
      category: z.string().min(1).max(128).optional().describe("Restrict matches to a category (OST_StructuralFoundation or 'Structural Foundations')"),
    })
    .strict(),
]);

const systemTypeRef = z
  .object({
    name: z.string().min(1).max(256).describe("Type name, wildcards allowed, e.g. '150mm Concrete With 54mm Metal Deck' or 'AC_S-*'"),
    category: z.string().min(1).max(128).optional().describe("OST_Floors, OST_Walls, OST_StructuralFoundation, ... (recommended)"),
  })
  .strict();

export const copyFamiliesSchema = {
  sourceDocument: z.string().min(1).max(256).optional().describe("Title of another open project to copy from"),
  sourcePath: z
    .string()
    .min(1)
    .max(400)
    .optional()
    .describe("Path of a .rvt/.rte to copy from; opened in the background (workshared files detached, worksets closed) and closed without saving - the file is never modified"),
  listOnly: z.boolean().optional().describe("Only list the source's loadable families (category, type count, instance count) and system types"),
  families: z.array(familyRef).max(200).optional().describe("Loadable families to copy with all their types"),
  categories: z
    .array(z.string().min(1).max(128))
    .max(30)
    .optional()
    .describe("Copy every loadable family of these categories (when families is omitted); also filters listOnly"),
  systemTypes: z.array(systemTypeRef).max(200).optional().describe("System types to copy (floor, wall, roof, foundation slab, wall foundation types ...)"),
  overwrite: z.boolean().optional().describe("Reload families that already exist in the active project (default: keep the existing family)"),
  saveToFolder: z.string().min(1).max(400).optional().describe("Also save each copied family as <folder>\\<family>.rfa"),
};

export function registerCopyFamiliesTool(server: McpServer) {
  server.tool(
    "copy_families",
    "Copy families and types from another project into the active one - e.g. the office's pile, footing or connection families from a reference model, or its floor/wall types. " +
      "Loadable families come with all their types (via the family editor, so parameters/formulas are kept); system types are copied with Copy/Paste (existing names are kept, not duplicated). " +
      "Source is an open project (sourceDocument) or a file path (sourcePath) opened in the background and closed unsaved. Start with listOnly:true to see what the source has. " +
      "Families already in the project are skipped unless overwrite. Optionally saves the families as .rfa files (saveToFolder) to build a library. Each item reports the family id and its types.",
    copyFamiliesSchema,
    async (args) => {
      if (!args.sourceDocument === !args.sourcePath) {
        return { content: [{ type: "text" as const, text: "Give exactly one of sourceDocument or sourcePath." }], isError: true };
      }
      if (!args.listOnly && !args.families?.length && !args.categories?.length && !args.systemTypes?.length) {
        return {
          content: [{ type: "text" as const, text: "Give families, categories or systemTypes to copy (or listOnly:true)." }],
          isError: true,
        };
      }
      return sendDocumentationCommand("copy_families", args, 600000);
    }
  );
}
