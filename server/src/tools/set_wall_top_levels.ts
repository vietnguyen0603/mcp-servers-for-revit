import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

const levelRef = z.union([elementIdSchema, z.string().min(1).max(256)]).describe("Level id or name");

const levelPair = z
  .object({
    from: levelRef.describe("Level the wall tops currently reach (id or name)"),
    to: levelRef.describe("Level to constrain those wall tops to (id or name)"),
    topOffset: z.number().finite().optional().describe("Top offset in mm for this pair (overrides the default)"),
  })
  .strict();

export function registerSetWallTopLevelsTool(server: McpServer) {
  server.tool(
    "set_wall_top_levels",
    "Re-constrain wall tops from one level to another in one undo step - e.g. make interior walls that run up to '3RD' stop at '3RD TOP PLATE' below the floor. " +
      "Give explicit mapping pairs, or a suffix that maps every level X to an existing level 'X<suffix>' (e.g. ' TOP PLATE'). " +
      "A wall is retargeted when its current top (top constraint + offset, or base + unconnected height) is within tolerance of a 'from' level; its Top Constraint becomes the 'to' level with topOffset. " +
      "Filter with wallFunction (default interior) and elementIds. Walls that run past a mapped level are listed in spanningWallIds, not split. " +
      "Use dryRun: true first to see which walls would change. Re-running is safe: walls already at the 'to' level are not matched.",
    {
      mapping: z.array(levelPair).max(100).optional().describe("Explicit from -> to level pairs"),
      suffix: z.string().min(1).max(64).optional().describe("Map each level X to the existing level X + suffix (used when mapping is not given)"),
      wallFunction: z.enum(["interior", "exterior", "all"]).default("interior").describe("Wall type Function filter"),
      elementIds: z.array(elementIdSchema).max(10000).optional().describe("Only consider these walls"),
      topOffset: z.number().finite().default(0).describe("Default top offset in mm from the 'to' level"),
      tolerance: z.number().positive().max(1000).default(25).describe("How close (mm) a wall top must be to a 'from' level to match"),
      dryRun: z.boolean().default(false).describe("Report the walls that would change without modifying the model"),
    },
    async (args) => {
      if (!args.mapping?.length && !args.suffix) {
        return { content: [{ type: "text" as const, text: "Give mapping pairs or a suffix." }], isError: true };
      }
      return sendDocumentationCommand("set_wall_top_levels", args, 300000);
    }
  );
}
