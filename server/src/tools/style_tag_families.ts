import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

const colorSchema = z
  .union([
    z.string().regex(/^(#?[0-9a-fA-F]{6}|\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}|black|white|red|green|blue)$/i),
    z.object({ r: z.number().int().min(0).max(255), g: z.number().int().min(0).max(255), b: z.number().int().min(0).max(255) }).strict(),
  ])
  .describe("Colour as '#RRGGBB', 'r,g,b' or {r,g,b}");

export const styleTagFamiliesShape = {
  families: z
    .array(z.string().min(1).max(256))
    .min(1)
    .max(500)
    .optional()
    .describe('Loaded family names to restyle (exact, case-insensitive), e.g. ["M_Structural Framing Tag","Column Tag"]'),
  categories: z
    .array(z.string().min(1).max(128))
    .min(1)
    .max(50)
    .optional()
    .describe(
      'Restyle every editable family of these categories, e.g. ["OST_StructuralFramingTags","OST_StructuralColumnTags","OST_StructuralFoundationTags","OST_MultiCategoryTags","OST_GridHeads","OST_LevelHeads","OST_ViewportLabel" (view titles),"OST_SectionHeads","OST_CalloutHeads"]'
    ),
  likeTextType: z
    .string()
    .min(1)
    .max(256)
    .optional()
    .describe('Project text type to copy font, size, width factor, bold, italic and underline from, e.g. "S-TEXT 2.5mm Arial"; explicit values below override it'),
  font: z.string().min(1).max(128).optional().describe('Font name, e.g. "Arial"'),
  textSizeMm: z.number().positive().max(100).optional().describe("Text height in paper mm, e.g. 2.5"),
  widthFactor: z.number().min(0.01).max(10).optional().describe("Width factor, e.g. 0.8"),
  bold: z.boolean().optional(),
  italic: z.boolean().optional(),
  underline: z.boolean().optional(),
  color: colorSchema.optional().describe("Text / label colour (also the colour of the text type's leader and border)"),
  lineWeight: z
    .number()
    .int()
    .min(1)
    .max(16)
    .optional()
    .describe("Pen 1-16 for tag lines: the text types' leader/border weight, the family category lines (tag boxes) and the project object style of the category (tag leaders)"),
  lineColor: colorSchema.optional().describe("Colour of the family category lines (tag boxes) and the project object style of the category"),
  background: z.enum(["opaque", "transparent"]).optional().describe("Text background of every text / label type"),
  familyCategoryLines: z
    .boolean()
    .optional()
    .default(true)
    .describe("Apply lineWeight / lineColor to the family category and its subcategories inside each family (default true)"),
  projectObjectStyles: z
    .boolean()
    .optional()
    .default(true)
    .describe(
      "Apply lineWeight / lineColor to the project's object style of each family category (default true) - Revit draws tag lines and leaders with the project's object style once the category exists in the project; this affects every family of that category"
    ),
  dryRun: z
    .boolean()
    .optional()
    .default(false)
    .describe("Only report the current values that would change, per family and text type, without editing or reloading"),
  maxFamilies: z.number().int().min(1).max(1000).optional().default(100).describe("Refuse to run if more families than this match (default 100)"),
};

export function registerStyleTagFamiliesTool(server: McpServer) {
  server.tool(
    "style_tag_families",
    "Make tag and other annotation families (tags, multi-category tags, grid / level heads, section / callout heads, view titles) match the project text style. Labels cannot be created through the API, but their text types can be edited: for each selected loaded family the tool opens it in the family editor, sets every text / label type (font, textSizeMm, widthFactor, bold, italic, underline, color, lineWeight, background), optionally the line weight / colour of the family category lines (tag boxes), reloads the family with overwrite and closes it. " +
      "Select families by name (families) or category (categories). likeTextType copies font/size/width/bold from a project text type such as 'S-TEXT 2.5mm Arial'. Reports per family the text types changed (old -> new values) and whether it was reloaded. Families nested inside tag families are not edited. Several undo steps (one per reloaded family).",
    styleTagFamiliesShape,
    async (args) => {
      if (!args.families?.length && !args.categories?.length) {
        return {
          content: [{ type: "text" as const, text: "style_tag_families: give families and/or categories." }],
          isError: true,
        };
      }
      const styleKeys = ["likeTextType", "font", "textSizeMm", "widthFactor", "bold", "italic", "underline", "color", "lineWeight", "lineColor", "background"] as const;
      if (!styleKeys.some((key) => args[key] !== undefined)) {
        return {
          content: [{ type: "text" as const, text: `style_tag_families: give likeTextType and/or at least one of ${styleKeys.slice(1).join(", ")}.` }],
          isError: true,
        };
      }
      return sendDocumentationCommand("style_tag_families", args, 600000);
    }
  );
}
