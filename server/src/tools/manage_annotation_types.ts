import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

export const ANNOTATION_TYPE_KINDS = ["text", "dimension", "spotElevation", "grid", "level", "viewport", "arrowhead"] as const;
export type AnnotationTypeKind = (typeof ANNOTATION_TYPE_KINDS)[number];

const TEXT_KINDS: AnnotationTypeKind[] = ["text", "dimension", "spotElevation"];

/** Which kinds each settings key applies to (checked again in Revit). */
export const SETTING_KINDS: Record<string, AnnotationTypeKind[]> = {
  font: TEXT_KINDS,
  textSize: TEXT_KINDS,
  bold: TEXT_KINDS,
  italic: TEXT_KINDS,
  underline: TEXT_KINDS,
  widthFactor: TEXT_KINDS,
  color: [...TEXT_KINDS, "level", "viewport"],
  background: ["text"],
  showBorder: ["text"],
  leaderBorderOffset: ["text"],
  tabSize: ["text"],
  leaderArrowhead: ["text", "spotElevation"],
  lineWeight: ["text", "dimension", "spotElevation", "level", "viewport"],
  textBackground: ["dimension", "spotElevation"],
  textOffset: ["dimension"],
  tickMark: ["dimension"],
  interiorTickMark: ["dimension"],
  tickLineWeight: ["dimension", "spotElevation"],
  witnessLineExtension: ["dimension"],
  witnessLineGap: ["dimension"],
  dimensionLineExtension: ["dimension"],
  centerlineSymbol: ["dimension"],
  centerlinePattern: ["dimension"],
  units: ["dimension", "spotElevation"],
  symbol: ["spotElevation", "level"],
  textOffsetFromLeader: ["spotElevation"],
  textOffsetFromSymbol: ["spotElevation"],
  indicator: ["spotElevation"],
  indicatorPosition: ["spotElevation"],
  bubble: ["grid"],
  bubbleEnd1: ["grid"],
  bubbleEnd2: ["grid"],
  nonPlanBubbles: ["grid"],
  centerSegment: ["grid"],
  centerSegmentWeight: ["grid"],
  centerSegmentColor: ["grid"],
  centerSegmentPattern: ["grid"],
  endSegmentWeight: ["grid"],
  endSegmentColor: ["grid"],
  endSegmentPattern: ["grid"],
  endSegmentLength: ["grid"],
  bubbleLineWeight: ["grid"],
  symbolEnd1: ["level"],
  symbolEnd2: ["level"],
  linePattern: ["level", "viewport"],
  elevationBase: ["level"],
  title: ["viewport"],
  showTitle: ["viewport"],
  showExtensionLine: ["viewport"],
  style: ["arrowhead"],
  tickSize: ["arrowhead"],
  widthAngle: ["arrowhead"],
  filled: ["arrowhead"],
  closed: ["arrowhead"],
  heavyEndPen: ["arrowhead"],
};

const mm = (what: string) => z.number().finite().min(0).max(100000).describe(what);
const pen = (what: string) => z.number().int().min(1).max(16).describe(`${what} (pen number 1-16)`);
const typeName = (what: string) =>
  z.union([z.string().min(1).max(256), z.number().int().positive()]).describe(`${what}; 'none' clears it`);
const color = z
  .union([
    z.string().regex(/^(#?[0-9a-fA-F]{6}|\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}|black|white|red|green|blue)$/i),
    z.object({ r: z.number().int().min(0).max(255), g: z.number().int().min(0).max(255), b: z.number().int().min(0).max(255) }).strict(),
  ])
  .describe("Colour as '#RRGGBB', 'r,g,b' or {r,g,b}");
const pattern = (what: string) => z.string().min(1).max(256).describe(`${what}: line pattern name, e.g. 'Solid', 'Dash', 'Center'`);

export const unitsSchema = z
  .object({
    unit: z
      .enum(["mm", "cm", "m", "ft", "in", "ftin", "fracIn", "deg", "degMin"])
      .optional()
      .describe("Display unit: mm for drawings, m for spot elevations (e.g. +12.500), ftin for feet-fractional inches, deg for angular"),
    accuracy: z
      .number()
      .positive()
      .max(1000)
      .optional()
      .describe("Rounding increment in the display unit: 1 (mm, no decimals), 5, 0.5, 0.001 (m with 3 decimals)"),
    rounding: z.enum(["nearest", "up", "down"]).optional().describe("Rounding method (default nearest)"),
    suppressTrailingZeros: z.boolean().optional().describe("12.50 -> 12.5"),
    suppressLeadingZeros: z.boolean().optional().describe("0.5 -> .5"),
    plusPrefix: z.boolean().optional().describe("Show '+' before positive values (spot elevations '+12.500')"),
    digitGrouping: z.boolean().optional().describe("Thousands separator (12,500)"),
    useProjectSettings: z.boolean().optional().describe("true: drop the type's own format and follow Project Units"),
  })
  .strict()
  .describe("Dimension/spot units format (overrides Project Units for this type)");

export const annotationSettingsSchema = z
  .object({
    // text-like (text, dimension, spotElevation)
    font: z.string().min(1).max(128).optional().describe("text/dimension/spotElevation: font name, e.g. 'Arial'"),
    textSize: z
      .number()
      .positive()
      .max(100)
      .optional()
      .describe("text/dimension/spotElevation: printed text height in mm (2.5 is readable on a 1:100 or 1:150 sheet; 1.8 is the minimum)"),
    bold: z.boolean().optional().describe("text/dimension/spotElevation"),
    italic: z.boolean().optional().describe("text/dimension/spotElevation"),
    underline: z.boolean().optional().describe("text/dimension/spotElevation"),
    widthFactor: z.number().positive().max(10).optional().describe("text/dimension/spotElevation: character width factor (1 = normal, 0.8 = condensed)"),
    color: color.optional().describe("text/dimension/spotElevation/level/viewport: colour '#RRGGBB' or {r,g,b}"),
    // text
    background: z.enum(["opaque", "transparent"]).optional().describe("text: background"),
    showBorder: z.boolean().optional().describe("text: draw a box around the note"),
    leaderBorderOffset: mm("text: leader/border offset in mm").optional(),
    tabSize: mm("text: tab size in mm").optional(),
    leaderArrowhead: typeName("text/spotElevation: arrowhead type name, e.g. 'Arrow Filled 15 Degree'").optional(),
    lineWeight: pen("text/dimension/spotElevation(leader)/level/viewport: line weight").optional(),
    // dimension
    textBackground: z.enum(["opaque", "transparent"]).optional().describe("dimension/spotElevation: text background"),
    textOffset: mm("dimension: gap between the dimension line and its text, mm").optional(),
    tickMark: typeName("dimension: tick mark arrowhead name, e.g. 'Diagonal 3mm' (create it with kind 'arrowhead')").optional(),
    interiorTickMark: typeName("dimension: interior tick mark arrowhead name").optional(),
    tickLineWeight: pen("dimension/spotElevation: tick mark line weight").optional(),
    witnessLineExtension: mm("dimension: witness line extension beyond the dimension line, mm").optional(),
    witnessLineGap: mm("dimension: witness line gap to element, mm").optional(),
    dimensionLineExtension: mm("dimension: dimension line extension past the witness lines, mm").optional(),
    centerlineSymbol: typeName("dimension: centerline symbol (annotation family type)").optional(),
    centerlinePattern: pattern("dimension: centerline pattern").optional(),
    units: unitsSchema.optional().describe("dimension/spotElevation: units format, e.g. {unit:'mm', accuracy:1} or {unit:'m', accuracy:0.001, plusPrefix:true}"),
    // spot elevation
    symbol: typeName("spotElevation: spot elevation symbol; level: level head, e.g. 'M_Level Head - Circle'").optional(),
    textOffsetFromLeader: mm("spotElevation: text offset from leader, mm").optional(),
    textOffsetFromSymbol: mm("spotElevation: text offset from symbol, mm").optional(),
    indicator: z.string().max(64).optional().describe("spotElevation: elevation indicator text, e.g. 'EL' (for a '+' sign on positive values use units.plusPrefix)"),
    indicatorPosition: z.enum(["prefix", "suffix"]).optional().describe("spotElevation: indicator before or after the value"),
    // grid
    bubble: typeName("grid: grid head family type, e.g. 'M_Grid Head - Circle'").optional(),
    bubbleEnd1: z.boolean().optional().describe("grid: default bubble at End 1 in plan views"),
    bubbleEnd2: z.boolean().optional().describe("grid: default bubble at End 2 in plan views"),
    nonPlanBubbles: z.string().min(1).max(64).optional().describe("grid: non-plan view bubbles default, e.g. 'Top', 'Bottom', 'Both', 'None'"),
    centerSegment: z.enum(["continuous", "none", "custom"]).optional().describe("grid: centre segment display ('none' = gapped grid with end segments only)"),
    centerSegmentWeight: pen("grid: centre segment weight").optional(),
    centerSegmentColor: color.optional().describe("grid: centre segment colour"),
    centerSegmentPattern: pattern("grid: centre segment pattern").optional(),
    endSegmentWeight: pen("grid: end segment weight (whole line when centerSegment is continuous)").optional(),
    endSegmentColor: color.optional().describe("grid: end segment colour"),
    endSegmentPattern: pattern("grid: end segment pattern").optional(),
    endSegmentLength: mm("grid: end segment length in mm (paper)").optional(),
    bubbleLineWeight: pen("grid: bubble line weight").optional(),
    // level
    symbolEnd1: z.boolean().optional().describe("level: symbol at End 1 by default"),
    symbolEnd2: z.boolean().optional().describe("level: symbol at End 2 by default"),
    linePattern: pattern("level/viewport: line pattern").optional(),
    elevationBase: z.enum(["projectBasePoint", "surveyPoint"]).optional().describe("level: elevation base"),
    // viewport
    title: typeName("viewport: view title family type").optional(),
    showTitle: z
      .union([z.boolean(), z.string().min(1).max(64)])
      .optional()
      .describe("viewport: true/false, or the Revit option text e.g. 'When multiple viewports on sheet'"),
    showExtensionLine: z.boolean().optional().describe("viewport: show the title extension line"),
    // arrowhead
    style: z
      .string()
      .min(1)
      .max(64)
      .optional()
      .describe("arrowhead: 'Diagonal', 'Arrow', 'Filled Arrow', 'Dot', 'Box', 'Heavy end tick mark', 'Datum triangle', 'Loop', 'Elevation Target'"),
    tickSize: mm("arrowhead: tick size in mm (paper), e.g. 3 for 'Diagonal 3mm'").optional(),
    widthAngle: z.number().min(0).max(180).optional().describe("arrowhead: arrow width angle in degrees (Arrow style)"),
    filled: z.boolean().optional().describe("arrowhead: fill tick"),
    closed: z.boolean().optional().describe("arrowhead: arrow closed"),
    heavyEndPen: pen("arrowhead: heavy end pen weight").optional(),
  })
  .strict();

const typeItemSchema = z
  .object({
    name: z
      .string()
      .min(1)
      .max(256)
      .describe("create: the new type name; update/setDefault: the existing type name"),
    sourceName: z.string().min(1).max(256).optional().describe("create: type to duplicate (default the first of the kind/style)"),
    style: z
      .enum(["linear", "angular", "radial", "diameter", "arcLength", "linearFixed", "spotElevation", "spotCoordinate", "spotSlope"])
      .optional()
      .describe("create: dimension/spot style to pick the default source from (default linear / spotElevation)"),
    ifExists: z.enum(["update", "reuse", "error"]).optional().describe("create: when the name exists - update it (default), reuse unchanged, or fail"),
    rename: z.string().min(1).max(256).optional().describe("update: new name"),
    settings: annotationSettingsSchema.optional().describe("Friendly settings; each key lists the kinds it applies to. Lengths mm (paper), angles degrees"),
    parameters: z
      .record(z.string().min(1).max(256), z.union([z.string().max(1024), z.number().finite(), z.boolean()]))
      .optional()
      .describe("Other type parameters by exact Revit name (numbers on lengths use project display units)"),
    setAsDefault: z.boolean().optional().describe("create/update: also make it the document default for its group"),
  })
  .strict();

type ManageArgs = {
  action: "list" | "create" | "update" | "setDefault";
  kind?: AnnotationTypeKind;
  types?: Array<z.infer<typeof typeItemSchema>>;
};

/** Cross-field checks the flat shape cannot express. Returns an error message or null. */
export function validateManageAnnotationTypes(args: ManageArgs): string | null {
  if (args.action === "list") return null;
  if (!args.kind) return `'kind' is required for ${args.action}`;
  if (!args.types || args.types.length === 0) return `'types' is required for ${args.action}`;
  if (args.action === "setDefault" && args.kind === "arrowhead") return "arrowhead types have no document default";
  for (const [index, item] of args.types.entries()) {
    for (const key of Object.keys(item.settings ?? {})) {
      const kinds = SETTING_KINDS[key];
      if (kinds && !kinds.includes(args.kind)) {
        return `types[${index}].settings.${key} does not apply to kind '${args.kind}' (applies to: ${kinds.join(", ")})`;
      }
    }
  }
  return null;
}

export function registerManageAnnotationTypesTool(server: McpServer) {
  server.tool(
    "manage_annotation_types",
    "Set up a project's drawing style: list, create (duplicate + set), update or make default the annotation types - text note types, dimension types (linear/angular/radial/diameter), spot elevation types, grid types, level types, viewport types and arrowhead (tick) types. " +
      "Typical fixes: dimension text too small at 1:150 -> update kind 'dimension' settings {textSize:2.5, font:'Arial', tickMark:'Diagonal 3mm', units:{unit:'mm', accuracy:1}}; create kind 'arrowhead' {style:'Diagonal', tickSize:3} first if the tick does not exist; " +
      "grid types {bubble:'M_Grid Head - Circle', bubbleEnd1:true, bubbleEnd2:false, centerSegment:'continuous', endSegmentWeight:3}; spot elevations {units:{unit:'m', accuracy:0.001, plusPrefix:true}}. " +
      "action 'list' (read-only) returns each type with its current values plus choices (arrowhead, grid head, level head, viewport title, spot symbol and line pattern names) - call it first to get exact names. " +
      "create duplicates sourceName (default the first type of the kind) as name; update edits an existing type; setDefault (or setAsDefault:true) makes it the default for new elements. " +
      "Text sizes and offsets are printed mm. Each type item is all-or-nothing; unknown or inapplicable parameters fail the item and list the type's editable parameter names. One undo step.",
    {
      action: z.enum(["list", "create", "update", "setDefault"]).describe("list (read-only), create, update or setDefault"),
      kind: z
        .enum(ANNOTATION_TYPE_KINDS)
        .optional()
        .describe("Type kind (required except for list; list without kind returns all kinds)"),
      nameContains: z.string().min(1).max(256).optional().describe("list: case-insensitive name filter"),
      style: z
        .enum(["linear", "angular", "radial", "diameter", "arcLength", "linearFixed", "spotElevation", "spotCoordinate", "spotSlope"])
        .optional()
        .describe("list: only dimension/spot types of this style"),
      includeAllParameters: z.boolean().optional().describe("list: also return every type parameter with its display value"),
      includeChoices: z.boolean().optional().describe("list: include available arrowhead/head/title/pattern names (default true)"),
      types: z.array(typeItemSchema).min(1).max(100).optional().describe("create/update/setDefault: the types to process"),
    },
    async (args) => {
      const error = validateManageAnnotationTypes(args as ManageArgs);
      if (error) {
        return { content: [{ type: "text" as const, text: `manage_annotation_types failed: ${error}` }], isError: true };
      }
      return sendDocumentationCommand("manage_annotation_types", args, 150000);
    }
  );
}
