import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

const namePatternSchema = z.union([
  z.string().min(1).max(256),
  z.array(z.string().min(1).max(256)).min(1).max(50),
]);

const familyOrTypeRef = z.union([
  z.number().int().positive(),
  z.string().min(1).max(512),
]).describe("Family or type id, 'Family : Type', or a family name");

const pairSchema = z
  .object({
    keep: familyOrTypeRef.describe("Family/type to keep: id, 'Family : Type' or family name"),
    merge: familyOrTypeRef.describe("Family/type merged into keep and deleted: id, 'Family : Type' or family name"),
  })
  .strict();

export const mergeDuplicateFamiliesSchema = {
  categories: z
    .array(z.string().min(1).max(128))
    .max(50)
    .optional()
    .describe("Only look for duplicates in these categories (recommended for speed), e.g. ['Callout Heads', 'OST_GenericAnnotation']"),
  familyNamePattern: namePatternSchema.optional().describe("Only families whose name matches (wildcards * ?)"),
  includeSystemTypes: z
    .boolean()
    .default(false)
    .describe("Also merge identical system types (text, dimension, filled region, wall ... types); group/assembly/import types never"),
  pairs: z
    .array(pairSchema)
    .min(1)
    .max(200)
    .optional()
    .describe("Explicit merges instead of the automatic search. Family pairs map each type to an identical type of the kept family"),
  force: z
    .boolean()
    .default(false)
    .describe("Pairs only: merge even when parameters/geometry differ (family pairs then map types by name)"),
  preferNamePattern: namePatternSchema
    .optional()
    .describe("Prefer keeping types/families whose name matches, e.g. '*AC*' (ranked after 'depended on')"),
  ignoreParameters: z
    .array(z.string().min(1).max(256))
    .max(50)
    .optional()
    .describe("Type parameter names left out of the comparison, e.g. ['Type Mark', 'Description']"),
  maxFamilyOpens: z
    .number()
    .int()
    .min(1)
    .max(2000)
    .default(300)
    .describe("Max families opened (read-only, EditFamily) to compare drawings"),
  dryRun: z.boolean().default(true).describe("Only report what would merge (default)"),
  maxResults: z.number().int().min(1).max(5000).default(500).describe("Cap on the listed merges / not-merged entries"),
};

export function registerMergeDuplicateFamiliesTool(server: McpServer) {
  server.tool(
    "merge_duplicate_families",
    "Find duplicate families/types in the same category and merge them: instances switch to the kept type, element-id type parameters and default types that point at the duplicate are re-pointed, " +
      "then the duplicate type is deleted (its family too when all of its types merged). Only merges when ALL type parameter values match (name/identity params skipped) AND the drawn geometry matches: " +
      "fingerprint = FamilySymbol geometry (solids/curves) + the family opened read-only (lines with style/length/end points, filled regions, text/labels, forms with extrusion depth/extents, nested placement, imports, other); " +
      "no ids or names, order- and direction-independent, numbers in mm compared within 0.05 + 1e-5 relative. When geometry differs, 'differences' says which aspect and how (e.g. forms: keep '... depth=400' vs merge '... depth=500'). " +
      "When every type of a duplicate family merges, the whole family is deleted (with its own types/subcategories/internal elements); otherwise the merged types are deleted. A deletion that would also remove real model content " +
      "(instances, views, tags/dimensions, grouped elements) is skipped and reported in deleted[].blockedBy. deleted[] lists each family/type removed (or wouldDelete in a dry run). " +
      "counts.familiesBefore/After = all loadable families in the document (same meaning in auto and pairs mode); counts.candidateFamilies = families whose types were considered. " +
      "Keep order: the type others depend on (referenced by other types, default type, used in groups), then preferNamePattern, then the un-numbered name (not 'X 2'), then the most used. " +
      "Instances inside detail/model groups or nested in other families are never switched; that type is kept and reported. " +
      "pairs [{keep, merge}] forces the choice (still compared unless force:true). dryRun (default true) reports counts: families before/after, types merged, instances switched, skipped, not merged (with differences), warnings.",
    mergeDuplicateFamiliesSchema,
    async (args) => {
      if (args.force && !args.pairs?.length) {
        return { content: [{ type: "text" as const, text: "force only applies to explicit pairs." }], isError: true };
      }
      return sendDocumentationCommand("merge_duplicate_families", args, 600000);
    }
  );
}
