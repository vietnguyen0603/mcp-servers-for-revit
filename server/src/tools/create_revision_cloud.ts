import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point3Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

const cloudSchema = z
  .object({
    rectangle: z
      .object({ min: point3Schema, max: point3Schema })
      .optional()
      .describe("Opposite corners (mm), aligned with the view"),
    points: z.array(point3Schema).min(3).max(500).optional().describe("Polygon vertices (mm)"),
  })
  .refine((cloud) => Boolean(cloud.rectangle) !== Boolean(cloud.points), {
    message: "Provide exactly one of rectangle or points",
  });

export function registerCreateRevisionCloudTool(server: McpServer) {
  server.tool(
    "create_revision_cloud",
    "Create revision clouds around rectangles or polygons in a view or on a sheet (millimetres, projected onto the view plane). Clouds are assigned to revisionId, or to the latest revision by default; issued revisions cannot receive new clouds. Not available in 3D views or schedules.",
    {
      viewId: elementIdSchema.optional().describe("View or sheet (default active view)"),
      revisionId: elementIdSchema.optional().describe("Revision (default latest)"),
      clouds: z.array(cloudSchema).min(1).max(200),
    },
    async (args) => sendDocumentationCommand("create_revision_cloud", args)
  );
}
