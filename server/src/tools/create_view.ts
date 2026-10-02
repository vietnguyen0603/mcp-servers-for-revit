import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import {
  elementIdSchema,
  point2Schema,
  point3Schema,
  sendDocumentationCommand,
} from "../utils/documentationSchemas.js";

const PLAN_TYPES = ["FloorPlan", "CeilingPlan", "StructuralPlan"];

const viewSchema = z
  .object({
    viewType: z
      .enum(["FloorPlan", "CeilingPlan", "StructuralPlan", "Section", "Elevation", "ThreeD", "Drafting"])
      .describe("Kind of view to create"),
    name: z.string().min(1).max(256).optional().describe("View name; must be unique for the view type"),
    levelId: elementIdSchema.optional().describe("Level for plan views"),
    levelName: z.string().min(1).max(256).optional().describe("Level name for plan views (if levelId is not given)"),
    sectionLine: z
      .object({ start: point2Schema, end: point2Schema })
      .optional()
      .describe(
        "Section only: cut line in plan (mm). The section looks to the left of start→end, so a line drawn left to right looks up/north."
      ),
    flip: z.boolean().optional().describe("Section only: look to the other side of the cut line"),
    bottomElevation: z
      .number()
      .finite()
      .optional()
      .describe("Section only: bottom of the crop in mm (default lowest level - 1000)"),
    topElevation: z
      .number()
      .finite()
      .optional()
      .describe("Section only: top of the crop in mm (default highest level + 3000)"),
    depth: z.number().positive().optional().describe("Section only: far clip depth in mm (default 1000)"),
    origin: point3Schema.optional().describe("Elevation only: marker location (mm)"),
    lookDirection: point2Schema
      .optional()
      .describe("Elevation only: plan direction the elevation looks towards, e.g. {x:0,y:1} looks north (default)"),
    planViewId: elementIdSchema
      .optional()
      .describe("Elevation only: plan view that hosts the marker (default active plan, else nearest floor plan)"),
    scale: z.number().int().min(1).max(100000).optional().describe("View scale denominator, e.g. 100 for 1:100"),
    detailLevel: z.enum(["Coarse", "Medium", "Fine"]).optional(),
    viewTemplateId: elementIdSchema.optional().describe("View template to apply (see list_views includeTemplates)"),
    viewFamilyTypeId: elementIdSchema
      .optional()
      .describe("View family type to use (see list_views includeViewFamilyTypes); default is the first of the family"),
  })
  .superRefine((view, ctx) => {
    if (PLAN_TYPES.includes(view.viewType) && view.levelId === undefined && view.levelName === undefined) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, message: `${view.viewType} requires levelId or levelName` });
    }
    if (view.viewType === "Section" && !view.sectionLine) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, message: "Section requires sectionLine" });
    }
    if (view.viewType === "Elevation" && !view.origin) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, message: "Elevation requires origin" });
    }
  });

export function registerCreateViewTool(server: McpServer) {
  server.tool(
    "create_view",
    "Create Revit views: floor, ceiling and structural plans (per level), sections (from a plan cut line), elevations (marker at a point looking in a plan direction), isometric 3D views and drafting views. Optionally set name, scale, detail level and view template. All coordinates are millimetres. Per-view errors are reported without discarding the other views, and the whole batch is one undo step.",
    {
      views: z.array(viewSchema).min(1).max(100).describe("Views to create"),
    },
    async (args) => sendDocumentationCommand("create_view", args)
  );
}
