import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point3Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerCreateSpotElevationsTool(server: McpServer) {
  server.tool(
    "create_spot_elevations",
    "Create spot elevations or spot coordinates on element faces in a view. In plan and 3D views the spot goes on the highest upward-facing face (at `point` if it lies on that face, else at the element location or face centre); in sections and elevations a given `point` snaps to the nearest face seen edge-on. Points are model millimetres. Each target succeeds or fails independently.",
    {
      viewId: elementIdSchema.optional().describe("View to annotate (default active view)"),
      kind: z.enum(["Elevation", "Coordinate"]).optional().default("Elevation"),
      targets: z
        .array(
          z.object({
            elementId: elementIdSchema.describe("Element to annotate, e.g. a floor, beam or foundation"),
            point: point3Schema.optional().describe("Point to snap the spot to (mm)"),
            bend: point3Schema.optional().describe("Leader bend point (mm)"),
            leaderEnd: point3Schema.optional().describe("Leader end / symbol location (mm)"),
            hasLeader: z.boolean().optional().default(false),
          })
        )
        .min(1)
        .max(500),
      spotTypeId: elementIdSchema.optional().describe("Spot elevation or spot coordinate type to use"),
    },
    async (args) => sendDocumentationCommand("create_spot_elevations", args)
  );
}
