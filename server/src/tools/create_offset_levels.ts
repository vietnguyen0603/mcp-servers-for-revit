import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

const sourceLevel = z
  .object({
    levelId: elementIdSchema.optional().describe("Source level element id"),
    name: z.string().min(1).max(256).optional().describe("Source level name (when levelId is not given)"),
    newName: z.string().min(1).max(256).optional().describe("Name of the offset level (default: source name + suffix)"),
    offset: z.number().finite().optional().describe("Offset from the source level in mm for this level (overrides the default)"),
  })
  .strict()
  .refine((l) => l.levelId !== undefined || l.name !== undefined, { message: "Give levelId or name" });

export function registerCreateOffsetLevelsTool(server: McpServer) {
  server.tool(
    "create_offset_levels",
    "Create a level at a fixed offset from each source level - e.g. 'TOP PLATE' levels 1' (-304.8 mm) below each floor level for walls to stop under the floor - in one undo step. " +
      "The new level takes the source's level type and, by default, the source's exact extents (2D and 3D, per view direction) and bubble visibility in every elevation, section and detail view. " +
      "Names default to source name + suffix (' TOP PLATE'). ifExists: 'skip' leaves an existing level untouched, 'update' moves it to the offset and re-matches extents, 'error' fails the item. " +
      "Offset levels are not Building Stories and get no plan views unless asked. Each item reports the level id, name, elevation (mm) and how many views were matched.",
    {
      levels: z.array(sourceLevel).min(1).max(200).describe("Source levels to offset"),
      offset: z.number().finite().default(-304.8).describe("Default offset in mm (negative = below the source; default -304.8 = 1')"),
      suffix: z.string().max(64).default(" TOP PLATE").describe("Appended to the source name when newName is not given"),
      ifExists: z.enum(["skip", "update", "error"]).default("skip").describe("What to do when a level with the target name exists"),
      matchExtents: z.boolean().default(true).describe("Copy the source level's extents and bubbles into every elevation/section view"),
      isBuildingStory: z.boolean().default(false).describe("Building Story flag for the offset levels"),
      planViews: z.enum(["none", "floor", "structural", "both"]).default("none").describe("Plan views to create for the offset levels"),
    },
    async (args) => sendDocumentationCommand("create_offset_levels", args, 120000)
  );
}
