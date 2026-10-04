import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

/** Kinds of project standards, in the order they are copied (dependencies first). */
export const PROJECT_STANDARD_KINDS = [
  "linePatterns",
  "fillPatterns",
  "materials",
  "lineStyles",
  "objectStyles",
  "arrowheads",
  "textTypes",
  "dimensionTypes",
  "gridTypes",
  "levelTypes",
  "viewportTypes",
  "wallTypes",
  "floorTypes",
  "viewFilters",
  "viewTemplates",
  "tagFamilies",
  "titleBlocks",
  "annotationSymbols",
] as const;

export const projectStandardKindSchema = z
  .enum(PROJECT_STANDARD_KINDS)
  .describe(
    "linePatterns, fillPatterns, materials, lineStyles (Lines subcategories), objectStyles (category weights/colours/patterns), " +
      "arrowheads, textTypes, dimensionTypes (linear, angular, radial, spot ...), gridTypes, levelTypes, viewportTypes, wallTypes, floorTypes, " +
      "viewFilters, viewTemplates (their filters come along), tagFamilies (OST_*Tags families), titleBlocks, annotationSymbols (generic annotations)"
  );

const kindRequestSchema = z.union([
  projectStandardKindSchema,
  z
    .object({
      kind: projectStandardKindSchema,
      names: z
        .array(z.string().min(1).max(256))
        .max(200)
        .optional()
        .describe("Name filters, wildcards * and ? allowed, e.g. ['S-*', '2.5mm Arial']. objectStyles: category names or OST_ names"),
      categories: z
        .array(z.string().min(1).max(128))
        .max(50)
        .optional()
        .describe("tagFamilies: only these tag categories, e.g. ['OST_StructuralFramingTags', 'Wall Tags']"),
    })
    .strict(),
]);

export const copyProjectStandardsSchema = {
  sourceDocument: z.string().min(1).max(256).optional().describe("Title of another open project or template to copy from"),
  sourcePath: z
    .string()
    .min(1)
    .max(400)
    .optional()
    .describe("Path of the office template / reference project (.rvt/.rte); opened in the background (workshared files detached, worksets closed) and closed without saving"),
  listOnly: z
    .boolean()
    .optional()
    .describe("Only list what the source has per kind (names, whether each already exists in the active project); kinds defaults to all"),
  kinds: z
    .array(kindRequestSchema)
    .min(1)
    .max(40)
    .optional()
    .describe("What to copy: a kind name for everything of that kind, or { kind, names, categories } to filter. Required unless listOnly"),
  onDuplicate: z
    .enum(["useDestination", "overwrite", "skip"])
    .optional()
    .describe(
      "When a name already exists in the active project. useDestination (default): keep the active project's item, and reuse its types when a copied item brings same-named dependencies. " +
        "overwrite: update existing items from the source - type parameters, pattern definitions, material graphics, line style values; view templates and filters are replaced and views re-pointed; families reloaded. " +
        "skip: keep existing items and skip any item whose copy would bring a same-named dependency"
    ),
};

export function registerCopyProjectStandardsTool(server: McpServer) {
  server.tool(
    "copy_project_standards",
    "Give the active project the office drawing standards from a template or reference project - like Revit's Transfer Project Standards: view templates, view filters, text/dimension/grid/level/viewport types, arrowheads, " +
      "line styles, line and fill patterns, object styles, materials, tag families, title blocks, annotation symbols and optionally wall/floor types. " +
      "Source is an open project (sourceDocument) or a .rvt/.rte path (sourcePath) opened in the background and never modified. Start with listOnly:true to see what the source has. " +
      "Existing names are kept unless onDuplicate:'overwrite'. Reports per kind what was copied, overwritten, renamed, skipped (exists) and failed with reasons, plus dependencies that came along. " +
      "One undo step (families load separately).",
    copyProjectStandardsSchema,
    async (args) => {
      if (!args.sourceDocument === !args.sourcePath) {
        return { content: [{ type: "text" as const, text: "Give exactly one of sourceDocument or sourcePath." }], isError: true };
      }
      if (!args.listOnly && !args.kinds?.length) {
        return {
          content: [{ type: "text" as const, text: "Give kinds to copy, e.g. [\"viewTemplates\", \"textTypes\"] (or listOnly:true to see what the source has)." }],
          isError: true,
        };
      }
      return sendDocumentationCommand("copy_project_standards", args, 600000);
    }
  );
}
