import { z } from "zod";
import { point2Schema } from "./documentationSchemas.js";

/** Schemas shared by place_viewport and update_viewports. */

export const viewportAnchorSchema = z
  .object({
    viewPoint: point2Schema.describe("Point in the view's model coordinates (mm), e.g. a detail line end point"),
    sheetPoint: point2Schema.describe("Where that point must land on the sheet (sheet mm)"),
  })
  .strict()
  .describe(
    "Move the viewport so viewPoint lands exactly on sheetPoint, e.g. to line up the edges of several details. Works for drafting views too"
  );

export const detailNumberSchema = z
  .string()
  .trim()
  .min(1)
  .max(64)
  .describe("Viewport detail number on its sheet, e.g. '3'");
