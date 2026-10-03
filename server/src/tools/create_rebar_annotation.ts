import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point3Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

export function registerCreateRebarAnnotationTool(server: McpServer) {
  server.tool(
    "create_rebar_annotation",
    "Annotate rebar in a view. Mode Tag places one rebar tag per bar set at its centre plus offsetMm. Mode MultiRebar creates one multi-rebar annotation (dimension line plus tag) across all the bars; the dimension line runs across the bars in the view plane through dimensionLinePoint (default: centre of the bars plus offsetMm). Without rebarIds, all rebar visible in the view is used. Points are model millimetres.",
    {
      viewId: elementIdSchema.optional().describe("View to annotate (default active view)"),
      mode: z.enum(["Tag", "MultiRebar"]).default("Tag"),
      rebarIds: z.array(elementIdSchema).max(5000).optional().describe("Rebar to annotate (default: all rebar in the view)"),
      tagTypeId: elementIdSchema.optional().describe("Tag mode: rebar tag type (default category tag)"),
      multiRebarTypeId: elementIdSchema
        .optional()
        .describe("MultiRebar mode: multi-reference annotation type (default the first in the project)"),
      dimensionLinePoint: point3Schema.optional().describe("MultiRebar mode: a point on the dimension line"),
      tagHeadPoint: point3Schema
        .optional()
        .describe("MultiRebar mode: tag head position (default 10 paper mm from the dimension line)"),
      offsetMm: point3Schema.optional().describe("Offset applied to default tag/dimension positions"),
      addLeader: z.boolean().optional().describe("Leader on tags (Tag default false, MultiRebar default true)"),
    },
    async (args) => sendDocumentationCommand("create_rebar_annotation", args)
  );
}
