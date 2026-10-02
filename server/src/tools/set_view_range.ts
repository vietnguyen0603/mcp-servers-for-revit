import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

const levelRef = z
  .union([elementIdSchema, z.enum(["Current", "LevelAbove", "LevelBelow", "Unlimited"])])
  .describe("Level id, or Current, LevelAbove, LevelBelow, Unlimited");
const offsetMm = z.number().finite().optional();

export function registerSetViewRangeTool(server: McpServer) {
  server.tool(
    "set_view_range",
    "Set the view range of plan views: top, cut plane, bottom and view depth. Offsets are millimetres relative to each plane's associated level (the cut plane uses the view's level). Views whose view range is controlled by a view template are reported as errors. Returns the resulting range per view.",
    {
      views: z
        .array(
          z.object({
            viewId: elementIdSchema.describe("Plan view"),
            topMm: offsetMm.describe("Top offset (mm)"),
            cutPlaneMm: offsetMm.describe("Cut plane offset (mm)"),
            bottomMm: offsetMm.describe("Bottom offset (mm)"),
            viewDepthMm: offsetMm.describe("View depth offset (mm)"),
            topLevelId: levelRef.optional(),
            bottomLevelId: levelRef.optional(),
            viewDepthLevelId: levelRef.optional(),
          })
        )
        .min(1)
        .max(200),
    },
    async (args) => sendDocumentationCommand("set_view_range", args)
  );
}
