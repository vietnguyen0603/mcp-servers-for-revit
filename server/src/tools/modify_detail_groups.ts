import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { elementIdSchema, point3Schema, sendDocumentationCommand } from "../utils/documentationSchemas.js";

const elementIds = z.array(elementIdSchema).min(1).max(1000);

const operationSchema = z.discriminatedUnion("action", [
  z
    .object({
      action: z.literal("place"),
      groupTypeId: elementIdSchema.optional(),
      groupTypeName: z.string().min(1).max(256).optional(),
      viewId: elementIdSchema.optional().describe("Target view (default: request viewId, else active view)"),
      location: point3Schema.describe("Where the group origin lands (mm)"),
      sourceInstanceId: elementIdSchema.optional().describe("Instance to copy (default: one in the target view, else any)"),
    })
    .strict()
    .describe("Place a detail group by copying an existing instance of its type"),
  z
    .object({
      action: z.literal("create"),
      elementIds: elementIds.describe("View-specific elements of one view"),
      name: z.string().min(1).max(256).optional().describe("Name of the new group type"),
    })
    .strict(),
  z.object({ action: z.literal("ungroup"), elementIds: elementIds.describe("Detail group instances") }).strict(),
]);

export function registerModifyDetailGroupsTool(server: McpServer) {
  server.tool(
    "modify_detail_groups",
    "Place, create and ungroup detail groups. place copies an existing instance of the type (see list_detail_groups) into the target view and moves its origin to location; a type without any placed instance can only be placed in the active view. create groups view-specific elements and optionally names the new type; ungroup returns the member ids. Millimetres. All operations are one undo step; each reports its own success.",
    {
      viewId: elementIdSchema.optional().describe("Default target view for place"),
      operations: z
        .array(operationSchema)
        .min(1)
        .max(500)
        .superRefine((ops, ctx) =>
          ops.forEach((op, i) => {
            if (op.action === "place" && op.groupTypeId === undefined && op.groupTypeName === undefined) {
              ctx.addIssue({ code: z.ZodIssueCode.custom, path: [i], message: "place needs groupTypeId or groupTypeName" });
            }
          })
        ),
    },
    async (args) => sendDocumentationCommand("modify_detail_groups", args)
  );
}
