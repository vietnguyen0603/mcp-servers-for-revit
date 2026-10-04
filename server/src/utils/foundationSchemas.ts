import { z } from "zod";
import { point2Schema } from "./documentationSchemas.js";

/** Shared input pieces for create_slabs and create_foundations (all lengths mm). */

export const levelRefSchema = z
  .union([z.string().min(1).max(256), z.number().finite()])
  .describe(
    "Level: name (string), level element id, or elevation in mm (a number that is not a level id; when no level is within 1 mm the nearest level is used and the difference is added to the offset)"
  );

export const polygonSchema = z
  .array(point2Schema)
  .min(3)
  .max(2000)
  .describe("Closed polygon as {x,y} mm points (do not repeat the first point)");

export const segmentSchema = z
  .object({
    start: point2Schema,
    end: point2Schema,
    mid: point2Schema.optional().describe("A point on the arc between start and end; omit for a straight segment"),
  })
  .strict();

export const markSchema = z.union([z.string().max(256), z.number().finite()]).describe("Mark parameter value");

export const typeIdSchema = z.number().int().positive();
