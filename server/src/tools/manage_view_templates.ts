import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

/**
 * manage_view_templates: list / create / modify / apply view templates (and
 * modify plain views) in one call. Actions run in order inside one undoable
 * transaction, each isolated so one failure does not discard the others; a
 * template created by an earlier action can be referenced by name later.
 */

const rgbSchema = z
  .tuple([z.number().int().min(0).max(255), z.number().int().min(0).max(255), z.number().int().min(0).max(255)])
  .describe("Colour [r, g, b], each 0-255");

const patternName = z.string().min(1).max(256);

const lineSchema = z
  .object({
    weight: z.number().int().min(1).max(16).optional().describe("Pen weight 1-16"),
    color: rgbSchema.optional(),
    pattern: patternName
      .optional()
      .describe("Line pattern name, e.g. 'Solid', 'Dash', 'Hidden', 'Center' (case-insensitive)"),
  })
  .strict();

const fillLayerSchema = z
  .object({
    pattern: patternName
      .optional()
      .describe("Fill pattern name, e.g. '<Solid fill>' / 'Solid fill' / 'Diagonal up' (drafting patterns preferred)"),
    color: rgbSchema.optional(),
    visible: z.boolean().optional().describe("Show/hide this pattern layer (default true when pattern or color given)"),
  })
  .strict();

const fillSchema = fillLayerSchema
  .extend({
    background: fillLayerSchema.optional().describe("Background pattern layer"),
  })
  .strict()
  .describe("Fill override; a color without a pattern implies solid fill");

const overrideFields = {
  halftone: z.boolean().optional(),
  transparency: z.number().int().min(0).max(100).optional().describe("Surface transparency 0-100"),
  projectionLine: lineSchema.optional().describe("Projection (beyond the cut) lines"),
  cutLine: lineSchema.optional().describe("Cut lines (elements cut by the view's cut plane)"),
  surfaceFill: fillSchema.optional().describe("Surface (projection) fill patterns"),
  cutFill: fillSchema.optional().describe("Cut fill patterns, e.g. solid gray for cut columns/walls"),
  detailLevel: z
    .enum(["ByView", "Coarse", "Medium", "Fine"])
    .optional()
    .describe("Per-category/filter detail level override (ByView clears it)"),
  reset: z.boolean().optional().describe("Clear existing overrides first; otherwise unspecified fields are kept"),
};

const overridesSchema = z.object(overrideFields).strict();

const categorySchema = z
  .object({
    category: z
      .string()
      .min(1)
      .max(256)
      .describe(
        "Model or annotation category: display name ('Structural Framing', 'Grids'), BuiltInCategory ('OST_Grids'), or 'Category/Subcategory' (e.g. 'Structural Framing/Hidden Lines', 'Floors/Common Edges')"
      ),
    visible: z.boolean().optional().describe("Category visibility (V/G checkbox)"),
    ...overrideFields,
  })
  .strict();

const filterSchema = z
  .object({
    name: z.string().min(1).max(256).describe("Existing view filter name (rule- or selection-based); added if missing"),
    visible: z.boolean().optional().describe("Filter 'Visibility' checkbox"),
    enabled: z.boolean().optional().describe("Filter 'Enable Filter' checkbox (Revit 2021+)"),
    remove: z.boolean().optional().describe("Remove the filter from the template/view"),
    overrides: overridesSchema.optional().describe("Graphic overrides for elements passing the filter"),
  })
  .strict();

const levelRef = z
  .union([elementIdSchema, z.enum(["Current", "LevelAbove", "LevelBelow", "Unlimited"])])
  .describe("Level id, or Current, LevelAbove, LevelBelow, Unlimited");

const viewRangeSchema = z
  .object({
    topMm: z.number().finite().optional().describe("Top offset (mm) from its level"),
    cutPlaneMm: z.number().finite().optional().describe("Cut plane offset (mm) from the view level"),
    bottomMm: z.number().finite().optional().describe("Bottom offset (mm) from its level"),
    viewDepthMm: z.number().finite().optional().describe("View depth offset (mm) from its level"),
    topLevelId: levelRef.optional(),
    bottomLevelId: levelRef.optional(),
    viewDepthLevelId: levelRef.optional(),
  })
  .strict()
  .describe("Plan view range, same shape as set_view_range (without viewId). Templates support relative levels only");

const templateRef = {
  templateId: elementIdSchema.optional().describe("View template id"),
  templateName: z.string().min(1).max(256).optional().describe("View template name (exact, case-insensitive)"),
};

const listAction = z
  .object({
    action: z.literal("list"),
    nameContains: z.string().max(256).optional().describe("Only templates whose name contains this text"),
    templateIds: z.array(elementIdSchema).max(500).optional().describe("Only these templates"),
    verbose: z
      .boolean()
      .optional()
      .describe("Include full override details per category (default: names of hidden/overridden categories only)"),
  })
  .strict();

const createAction = z
  .object({
    action: z.literal("create"),
    name: z
      .string()
      .min(1)
      .max(256)
      .describe("New template name (no \\ : { } [ ] | ; < > ? ` ~)"),
    viewType: z
      .enum(["FloorPlan", "StructuralPlan", "CeilingPlan", "Section", "Elevation", "ThreeD", "Drafting"])
      .optional()
      .describe("Required unless fromViewId/fromTemplate is given; a temporary view of this type is created and deleted"),
    fromViewId: elementIdSchema.optional().describe("Create the template from this existing (non-template) view"),
    fromTemplate: z
      .union([elementIdSchema, z.string().min(1).max(256)])
      .optional()
      .describe("Duplicate this existing template (id or name)"),
    ifExists: z
      .enum(["error", "reuse"])
      .optional()
      .describe("When a template with this name exists: error (default) or reuse it (later actions can still modify it)"),
  })
  .strict();

const modifyAction = z
  .object({
    action: z.literal("modify"),
    ...templateRef,
    viewIds: z
      .array(elementIdSchema)
      .max(500)
      .optional()
      .describe("Modify these views (templates or ordinary views) instead of templateId/templateName"),
    scale: z
      .union([z.number().int().min(1).max(24000), z.string().regex(/^\s*1\s*:\s*\d+\s*$/)])
      .optional()
      .describe("View scale denominator (150) or '1:150'"),
    detailLevel: z.enum(["Coarse", "Medium", "Fine"]).optional(),
    discipline: z
      .enum(["Architectural", "Structural", "Mechanical", "Electrical", "Plumbing", "Coordination"])
      .optional(),
    displayStyle: z
      .enum(["Wireframe", "HiddenLine", "Shaded", "ShadedWithEdges", "ConsistentColors", "Realistic"])
      .optional()
      .describe("Visual style"),
    showHiddenLines: z
      .enum(["None", "ByDiscipline", "All"])
      .optional()
      .describe("'Show Hidden Lines' view parameter; ByDiscipline in a Structural view shows beams below slabs dashed"),
    underlay: z
      .object({
        baseLevel: z
          .union([elementIdSchema, z.string().min(1).max(256)])
          .optional()
          .describe("Level id or name, or 'None' to clear (plan views only, not templates)"),
        topLevel: z
          .union([elementIdSchema, z.string().min(1).max(256)])
          .optional()
          .describe("Level id or name, or 'Unbounded'"),
        orientation: z.enum(["LookingDown", "LookingUp"]).optional(),
      })
      .strict()
      .optional()
      .describe("Plan underlay"),
    viewRange: viewRangeSchema.optional(),
    categories: z
      .array(categorySchema)
      .max(300)
      .optional()
      .describe("Visibility/Graphics per category (model and annotation); unspecified fields are kept"),
    filters: z.array(filterSchema).max(100).optional().describe("View filter visibility/overrides; existing filters only"),
    controlled: z
      .object({
        include: z
          .array(z.string().min(1).max(256))
          .max(100)
          .optional()
          .describe("Parameters the template should control (tick 'Include')"),
        exclude: z
          .array(z.string().min(1).max(256))
          .max(100)
          .optional()
          .describe("Parameters the template should NOT control (untick 'Include')"),
      })
      .strict()
      .optional()
      .describe(
        "Template 'Include' checkboxes (templates only). Friendly names: 'View Scale', 'Detail Level', 'Discipline', 'View Range', 'V/G Overrides Model', 'V/G Overrides Annotation', 'V/G Overrides Filters' (or 'Filters'), 'Show Hidden Lines', 'Underlay Orientation', 'Phase Filter', 'Visual Style'... or BuiltInParameter names; 'all' selects every template parameter"
      ),
  })
  .strict();

const applyAction = z
  .object({
    action: z.literal("apply"),
    ...templateRef,
    viewIds: z.array(elementIdSchema).min(1).max(1000).describe("Views to receive the template"),
    applyPropertiesOnly: z
      .boolean()
      .optional()
      .describe("Copy the template's properties once without assigning it (default false = assign)"),
  })
  .strict();

const actionSchema = z.discriminatedUnion("action", [listAction, createAction, modifyAction, applyAction]);

type Action = z.infer<typeof actionSchema>;

/** Characters Revit refuses in view and view template names. */
export const REVIT_PROHIBITED_NAME_CHARS = ["\\", ":", "{", "}", "[", "]", "|", ";", "<", ">", "?", "`", "~"] as const;

/** Error text when a view/template name contains Revit-prohibited characters, else null. */
export function revitViewNameError(name: string): string | null {
  const found = REVIT_PROHIBITED_NAME_CHARS.filter((c) => name.includes(c));
  if (found.length === 0) return null;
  return `name '${name}' contains ${found.map((c) => `'${c}'`).join(" ")}; Revit view/template names cannot contain ${REVIT_PROHIBITED_NAME_CHARS.join(" ")}`;
}

/** Cross-field checks zod's discriminated union cannot express; returns error messages. */
export function validateViewTemplateActions(actions: Action[]): string[] {
  const errors: string[] = [];
  actions.forEach((a, i) => {
    const at = `actions[${i}] (${a.action})`;
    const checkName = (field: string, value: unknown) => {
      if (typeof value !== "string") return;
      const error = revitViewNameError(value);
      if (error) errors.push(`${at}: ${field} ${error}`);
    };
    if (a.action === "create") {
      checkName("name", a.name);
      checkName("fromTemplate", a.fromTemplate);
    }
    if (a.action === "modify" || a.action === "apply") checkName("templateName", a.templateName);
    if (a.action === "create") {
      if (a.fromViewId !== undefined && a.fromTemplate !== undefined)
        errors.push(`${at}: give fromViewId or fromTemplate, not both`);
      else if (a.viewType === undefined && a.fromViewId === undefined && a.fromTemplate === undefined)
        errors.push(`${at}: requires viewType, fromViewId or fromTemplate`);
    }
    if (a.action === "modify") {
      const hasTemplate = a.templateId !== undefined || a.templateName !== undefined;
      if (hasTemplate === (a.viewIds !== undefined && a.viewIds.length > 0))
        errors.push(`${at}: give templateId/templateName OR viewIds`);
      const settings = [
        "scale",
        "detailLevel",
        "discipline",
        "displayStyle",
        "showHiddenLines",
        "underlay",
        "viewRange",
        "categories",
        "filters",
        "controlled",
      ] as const;
      if (!settings.some((key) => a[key] !== undefined)) errors.push(`${at}: nothing to change`);
      if (a.controlled && a.viewIds?.length) errors.push(`${at}: 'controlled' applies to templates only`);
    }
    if (a.action === "apply" && a.templateId === undefined && a.templateName === undefined)
      errors.push(`${at}: requires templateId or templateName`);
  });
  return errors;
}

export function registerManageViewTemplatesTool(server: McpServer) {
  server.tool(
    "manage_view_templates",
    `List, create, modify and apply view templates in one call (actions run in order, one undo step; each action is isolated so one failure does not discard the others; a template created earlier in the call can be referenced by templateName later). Use it to set up a drawing style when the project has no suitable template, e.g. a 'S-FRAMING PLAN 1-150' StructuralPlan template (no ':' in names): create -> modify {scale:150, detailLevel:'Medium', discipline:'Structural', showHiddenLines:'ByDiscipline', categories:[{category:'Structural Columns', cutFill:{pattern:'<Solid fill>', color:[128,128,128]}, cutLine:{weight:5}}, {category:'Floors', projectionLine:{weight:1}}, {category:'Grids', visible:true}]} -> apply {viewIds}.
Actions:
- list: templates with type, scale, detail level, discipline, controlled parameters, filters and hidden/overridden categories.
- create: from a temporary view of viewType, from an existing view (fromViewId) or by duplicating a template (fromTemplate).
- modify: templateId/templateName, or viewIds to modify ordinary views. Category/filter overrides merge with existing ones unless reset:true.
- apply: assign the template to views, or copy its properties once (applyPropertiesOnly).
Patterns are names, colors [r,g,b], lengths mm. Category and subcategory names ignore case, extra spaces and surrounding <> ('Structural Framing/Hidden Lines' finds '<Hidden Lines>'). Unknown category/pattern/parameter names are reported as warnings with suggestions; the rest of the action still applies.
Revit stores every plan template (floor and structural plan) with viewType FloorPlan; templateKind (e.g. 'Plan (Structural discipline)') tells them apart by discipline. After create/modify a warning flags Floors/Structural Foundations overrides (transparency >= 50, or hidden surface pattern + transparency) that make beams under slabs look solid instead of dashed; fix with {category:'Floors', reset:true, transparency:0}.
Names cannot contain \\ : { } [ ] | ; < > ? \` ~.`,
    {
      actions: z.array(actionSchema).min(1).max(50).describe("Actions executed in order"),
    },
    async (args) => {
      const errors = validateViewTemplateActions(args.actions);
      if (errors.length > 0) {
        return {
          content: [{ type: "text" as const, text: `manage_view_templates: ${errors.join("; ")}` }],
          isError: true,
        };
      }
      return sendDocumentationCommand("manage_view_templates", args, 300000);
    }
  );
}
