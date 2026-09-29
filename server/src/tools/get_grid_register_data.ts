import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import {
  assertRegisterRequestSize,
  buildSharedRegisterParams,
  sharedRegisterInputShape,
} from "../utils/registerSchemas.js";

const axisAssignmentSchema = z.object({
  axisFamily: z
    .string()
    .min(1)
    .max(64)
    .describe(
      "Axis family label to assign, for example 'x', 'y', 'skew', or a project-specific family name."
    ),
  gridNames: z
    .array(z.string().min(1).max(128))
    .min(1)
    .max(256)
    .describe("Grid names belonging to this axis family."),
});

export function registerGetGridRegisterDataTool(server: McpServer) {
  server.tool(
    "get_grid_register_data",
    "Return register-ready grid data from the current Revit project: one record per named grid instance, including axis family, direction, and coordinate in millimetres (mm). Grid curves are preserved for curved, radial, and skewed grids where scalar coordinates do not apply.",
    {
      ...sharedRegisterInputShape,
      originGridUniqueId: z
        .string()
        .min(1)
        .max(128)
        .optional()
        .describe(
          "UniqueId of the grid to use as coordinate zero. Takes precedence over originGridName. When omitted, absolute project coordinates are returned and no origin is invented."
        ),
      originGridName: z
        .string()
        .min(1)
        .max(128)
        .optional()
        .describe(
          "Name of the grid to use as coordinate zero when originGridUniqueId is not given. Duplicate names are rejected as ambiguous."
        ),
      axisAssignments: z
        .array(axisAssignmentSchema)
        .max(128)
        .refine(
          (assignments) =>
            new Set(assignments.map((entry) => entry.axisFamily)).size ===
            assignments.length,
          { message: "axisAssignments may not repeat an axisFamily" }
        )
        .optional()
        .describe(
          "Optional explicit grid-name to axis-family assignments. When omitted, straight orthogonal grids are classified automatically by direction clustering under the angular tolerance."
        ),
    },
    async (args, extra) => {
      try {
        const params = {
          ...buildSharedRegisterParams(args),
          originGridUniqueId: args.originGridUniqueId ?? null,
          originGridName: args.originGridName ?? null,
          axisAssignments: args.axisAssignments ?? [],
        };

        assertRegisterRequestSize("get_grid_register_data", params);

        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_grid_register_data", params);
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
              text: `get_grid_register_data failed: ${
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
