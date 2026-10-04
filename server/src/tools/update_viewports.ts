import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point2Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { detailNumberSchema, viewportAnchorSchema } from "../utils/viewportSchemas.js";

const CHANGE_KEYS = [
  "detailNumber",
  "viewportTypeId",
  "viewportTypeName",
  "center",
  "anchor",
  "labelOffset",
  "labelLineLength",
] as const;

const viewportUpdateSchema = z
  .object({
    viewportId: elementIdSchema.describe("Viewport element id (see list_sheets)"),
    detailNumber: detailNumberSchema.optional(),
    viewportTypeId: elementIdSchema.optional().describe("Viewport type id; wins over viewportTypeName"),
    viewportTypeName: z
      .string()
      .min(1)
      .max(256)
      .optional()
      .describe("Viewport type name, e.g. 'TT-Not To Scale': exact case-insensitive match, then a unique partial match"),
    center: point2Schema.optional().describe("New centre of the viewport box (sheet mm); applied before anchor"),
    anchor: viewportAnchorSchema.optional(),
    labelOffset: point2Schema
      .optional()
      .describe("Viewport title offset from the viewport's lower-left corner (sheet mm). Revit 2022+"),
    labelLineLength: z
      .number()
      .finite()
      .nonnegative()
      .max(2000)
      .optional()
      .describe("Length of the title underline (sheet mm). Revit 2022+"),
  })
  .strict()
  .superRefine((item, ctx) => {
    if (!CHANGE_KEYS.some((key) => item[key] !== undefined)) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        message: "Nothing to change: give at least one of " + CHANGE_KEYS.join(", "),
      });
    }
  });

export function registerUpdateViewportsTool(server: McpServer) {
  server.tool(
    "update_viewports",
    "Edit viewports already placed on sheets, in one undo step: detail numbers, viewport type (by id or name), box centre, exact anchoring and title position. " +
      "detailNumber: all requested numbers are applied without collisions (swaps and renumbering 1..n within the batch work); an item fails if its number is held by another viewport on the same sheet that is not renumbered in this call. " +
      "anchor: moves the viewport so a point of the view (model mm, e.g. a detail line end point) lands on a sheet point (mm) - use it to align details on a sheet precisely, drafting views included; the achieved sheet position is returned. center moves the box centre (applied before anchor). " +
      "labelOffset/labelLineLength (sheet mm, Revit 2022+) place the viewport title and size its underline. Viewport ids come from list_sheets. Returns per-item results with succeeded/failed counts.",
    {
      viewports: z.array(viewportUpdateSchema).min(1).max(500),
    },
    async (args) => sendDocumentationCommand("update_viewports", args)
  );
}
