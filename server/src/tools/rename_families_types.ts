import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

const namePatternSchema = z.union([
  z.string().min(1).max(256),
  z.array(z.string().min(1).max(256)).min(1).max(50),
]);

export const renameFamiliesTypesSchema = {
  find: z.string().min(1).max(256).describe("Text to find in the names (literal unless regex:true), e.g. 'TT' or '^TT-'"),
  replace: z.string().max(256).default("").describe("Replacement text; with regex:true $1/${name} group references work"),
  regex: z.boolean().default(false).describe("Treat find as a .NET regular expression (anchor with ^ to only touch prefixes)"),
  matchCase: z.boolean().default(true).describe("Case-sensitive matching"),
  target: z
    .enum(["family", "type", "both"])
    .default("both")
    .describe("family = loadable family names, type = family types and system types, both = all"),
  categories: z
    .array(z.string().min(1).max(128))
    .max(50)
    .optional()
    .describe("Only these categories (OST_GenericAnnotation, 'Callout Heads', 'Walls' ...)"),
  familyNamePattern: namePatternSchema
    .optional()
    .describe("Only families / types whose family name matches (wildcards * ?); for system types the family is e.g. 'Basic Wall', 'Text', 'Linear Dimension Style'"),
  includeSystemTypes: z
    .boolean()
    .default(true)
    .describe("Include system types (wall, floor, text, dimension, filled region, view, viewport ... types)"),
  alsoRename: z
    .array(z.enum(["linePatterns", "fillPatterns", "materials", "lineStyles"]))
    .max(4)
    .default([])
    .describe("Also rename these (lineStyles can only be listed: the API can't rename subcategories)"),
  dryRun: z.boolean().default(true).describe("Only list old -> new names, collisions and blocked items (default)"),
  maxResults: z.number().int().min(1).max(20000).default(1000).describe("Cap on the listed renames"),
};

export function registerRenameFamiliesTypesTool(server: McpServer) {
  server.tool(
    "rename_families_types",
    "Find/replace on family, type and system type names, e.g. when converting a firm template (TT -> AC): '_TT-SY-Callout Head' -> '_AC-SY-Callout Head' (find 'TT', replace 'AC'), " +
      "or drop a leading 'TT-' from type names (find '^TT-', regex:true, target:'type'). Covers loadable families and their types and system types (walls, floors, text, dimensions, filled regions, view and viewport types ...), " +
      "optionally line patterns, fill patterns and materials. Filter by categories, family name pattern and target (family|type|both). " +
      "dryRun (default true) lists old -> new; renames that would collide with an existing name in the same scope are skipped and reported (merge_duplicate_families can merge such duplicates), invalid names are reported. " +
      "Names that can't be renamed in place are listed separately in cannotRenameInPlace: shared parameters (GUID-bound, rename in the shared parameter file), DWG/CAD import and link names, line styles. One undo step.",
    renameFamiliesTypesSchema,
    async (args) => sendDocumentationCommand("rename_families_types", args, 300000)
  );
}
