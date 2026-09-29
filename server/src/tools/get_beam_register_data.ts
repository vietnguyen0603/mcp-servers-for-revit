import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import {
  SUPPORT_CATEGORIES,
  assertRegisterRequestSize,
  buildSharedRegisterParams,
  sharedRegisterInputShape,
} from "../utils/registerSchemas.js";

export function registerGetBeamRegisterDataTool(server: McpServer) {
  server.tool(
    "get_beam_register_data",
    "Return register-ready beam data from the current Revit project: one record per physical beam instance, including mark/type, section size, level, axis/from/to grids, model and clear lengths, start/end coordinates and supports in millimetres (mm). Clear span is the face-to-face distance between support solids and is null when support faces cannot be established.",
    {
      ...sharedRegisterInputShape,
      beamTypeAliases: z
        .record(z.string().min(1).max(64), z.string().min(1).max(128))
        .refine((aliases) => Object.keys(aliases).length <= 32, {
          message: "beamTypeAliases may contain at most 32 keys",
        })
        .optional()
        .describe(
          "Optional mapping from project-specific beam type labels/aliases (for example 'HB', 'VB', 'SP') to a canonical register beam type. At most 32 entries; keys are matched case-insensitively."
        ),
      supportCategoryPrecedence: z
        .array(z.enum(SUPPORT_CATEGORIES))
        .max(SUPPORT_CATEGORIES.length)
        .refine(
          (order) => new Set(order).size === order.length,
          { message: "supportCategoryPrecedence may not repeat a category" }
        )
        .optional()
        .describe(
          "Optional category ordering (subset of 'wall', 'column', 'beam') used only to break ties between geometrically equivalent support candidates. Defaults to empty, so actual geometric contact is always preferred."
        ),
    },
    async (args, extra) => {
      try {
        const params = {
          ...buildSharedRegisterParams(args),
          beamTypeAliases: args.beamTypeAliases ?? {},
          supportCategoryPrecedence: args.supportCategoryPrecedence ?? [],
        };

        assertRegisterRequestSize("get_beam_register_data", params);

        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_beam_register_data", params);
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(response, null, 2),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `get_beam_register_data failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
