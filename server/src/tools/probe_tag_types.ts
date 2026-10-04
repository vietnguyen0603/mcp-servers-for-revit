import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { categoryNameSchema } from "../utils/elementFilterSchemas.js";

export const probeTagTypesShape = {
  category: categoryNameSchema
    .optional()
    .describe('Category whose tag types to probe, e.g. "OST_StructuralFraming", "StructuralColumns", "StructuralFoundation" (default: the sample element\'s category)'),
  elementId: elementIdSchema
    .optional()
    .describe("Sample element to tag (default: the first element of the category visible in the view)"),
  viewId: elementIdSchema.optional().describe("View to probe in (default active view; must be able to show tags, e.g. a plan)"),
  tagTypeIds: z
    .array(elementIdSchema)
    .min(1)
    .max(200)
    .optional()
    .describe("Probe only these tag types (default: every loaded tag type of the category's tag category)"),
  includeMultiCategory: z
    .boolean()
    .optional()
    .default(true)
    .describe("Also probe multi-category tag types (default true)"),
  probeLengths: z
    .boolean()
    .optional()
    .default(true)
    .describe("Also give the type's length parameters (b, h, ...) distinct test values to detect dimension labels (default true)"),
  maxTagTypes: z.number().int().min(1).max(200).optional().default(60).describe("Probe at most this many tag types"),
};

export function registerProbeTagTypesTool(server: McpServer) {
  server.tool(
    "probe_tag_types",
    "Find out which parameter(s) each loaded tag type displays, since tag-family labels cannot be read or created through the API. " +
      "Inside a transaction that is always rolled back (the model is left unchanged), it places one temporary tag of every tag type for the category (plus multi-category tags) on a sample element, reads the tag text, " +
      "then writes distinct marker values into Mark, Comments, Type Mark, Type Comments, Description, every other writable text parameter of the element and its type (incl. shared parameters), the type name, the family name and (probeLengths) the type's length parameters, and reads the tag text again. " +
      "Returns per tag type: family, type, id, displayedText (real values), template (e.g. '{Mark}-{b}x{h}'), detectedParameters, and a one-line summary such as 'M_Structural Framing Tag: Boxed -> Mark'. " +
      "Use it to pick the tag type to pass to tag_elements, e.g. one that shows Mark or a project parameter filled by set_parameters.",
    probeTagTypesShape,
    async (args) => {
      if (args.category === undefined && args.elementId === undefined) {
        return {
          content: [{ type: "text" as const, text: "probe_tag_types: give category and/or elementId." }],
          isError: true,
        };
      }
      return sendDocumentationCommand("probe_tag_types", args, 180000);
    }
  );
}
