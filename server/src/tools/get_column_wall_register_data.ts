import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import {
  assertRegisterRequestSize,
  buildSharedRegisterParams,
  sharedRegisterInputShape,
} from "../utils/registerSchemas.js";

export function registerGetColumnWallRegisterDataTool(server: McpServer) {
  server.tool(
    "get_column_wall_register_data",
    "Return register-ready column and wall data from the current Revit project: one record per physical column or wall leg instance, including mark, family/type, shape, leg number, plan dimensions in millimetres (mm), nearest reference grids, signed face offsets, extents, centre point, and notes.",
    {
      ...sharedRegisterInputShape,
      includeArchitecturalColumns: z
        .boolean()
        .default(false)
        .describe(
          "When true, include architectural columns (OST_Columns) in addition to structural columns (OST_StructuralColumns). Defaults to false."
        ),
      corePrefixes: z
        .array(z.string().min(1).max(32))
        .max(64)
        .optional()
        .describe(
          "Optional project mark prefixes identifying multi-leg core/shear walls, for example ['CW', 'SW']. Used only when grouping legs that have no explicit model grouping. At most 64 prefixes."
        ),
    },
    async (args, extra) => {
      try {
        const params = {
          ...buildSharedRegisterParams(args),
          includeArchitecturalColumns: args.includeArchitecturalColumns ?? false,
          corePrefixes: args.corePrefixes ?? [],
        };

        assertRegisterRequestSize("get_column_wall_register_data", params);

        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand(
            "get_column_wall_register_data",
            params
          );
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
              text: `get_column_wall_register_data failed: ${
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
