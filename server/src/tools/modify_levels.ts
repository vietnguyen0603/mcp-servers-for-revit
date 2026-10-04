import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

const levelEdit = z
  .object({
    levelId: elementIdSchema.optional().describe("Level element id"),
    name: z.string().min(1).max(256).optional().describe("Current level name (when levelId is not given)"),
    newName: z.string().min(1).max(256).optional().describe("New level name"),
    elevation: z.number().finite().optional().describe("New elevation in mm from the project base point"),
    isBuildingStory: z.boolean().optional().describe("Set the Building Story flag"),
    structuralPlan: z.boolean().optional().describe("true: create a structural plan view for the level if it has none"),
    floorPlan: z.boolean().optional().describe("true: create a floor plan view for the level if it has none"),
  })
  .strict()
  .refine((l) => l.levelId !== undefined || l.name !== undefined, { message: "Give levelId or name" });

export function registerModifyLevelsTool(server: McpServer) {
  server.tool(
    "modify_levels",
    "Rename levels, move them to a new elevation (mm), set Building Story, and create missing structural or floor plan views - in one undo step. " +
      "Identify each level by levelId or current name. structuralPlan/floorPlan: true only adds a view when the level has none of that kind (false never deletes views). " +
      "Each item reports levelId, name, elevation (mm), what changed and the ids of created views.",
    {
      levels: z.array(levelEdit).min(1).max(200).describe("Level edits"),
    },
    async (args) => sendDocumentationCommand("modify_levels", args)
  );
}
