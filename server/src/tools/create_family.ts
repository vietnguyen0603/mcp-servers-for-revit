import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

const parameterName = z.string().min(1).max(64);
const lengthMm = z.number().positive().max(500000);

const typeItem = z
  .object({
    name: z.string().min(1).max(256).describe("Type name, e.g. 'SGBR-01 1500x2800 L78000'"),
    width: lengthMm.optional().describe("Rectangular: Width in mm"),
    length: lengthMm.optional().describe("Rectangular: Length in mm"),
    diameter: lengthMm.optional().describe("Circular: Diameter in mm"),
    depth: lengthMm.optional().describe("Depth (pile length) in mm; ignored when depth is an instance parameter"),
  })
  .strict();

export const createFamilySchema = {
  name: z
    .string()
    .min(1)
    .max(120)
    .regex(/^[^\\/:*?"<>|]+$/, "Family name must be a valid file name")
    .describe("Family name (also the .rfa file name), e.g. 'MCP_Pile-Rectangular'"),
  kind: z
    .enum(["foundation", "genericModel"])
    .optional()
    .describe("Template: 'foundation' (Metric Structural Foundation, default - piles, barrettes, footings) or 'genericModel'"),
  shape: z.enum(["rectangular", "circular"]).optional().describe("Plan profile (default rectangular)"),
  direction: z
    .enum(["down", "up"])
    .optional()
    .describe("Extrusion direction from the insertion point (default down for foundation - the origin is the pile top - up for generic model)"),
  width: lengthMm.optional().describe("Default Width mm (rectangular, default 1200)"),
  length: lengthMm.optional().describe("Default Length mm (rectangular, default 2800)"),
  diameter: lengthMm.optional().describe("Default Diameter mm (circular, default 1000)"),
  depth: lengthMm.optional().describe("Default Depth mm (default 10000)"),
  instanceDepth: z
    .boolean()
    .optional()
    .describe("Make Depth an instance parameter (one type, pile length per instance); default false = type parameter"),
  widthParameter: parameterName.optional().describe("Name of the width parameter (default 'Width')"),
  lengthParameter: parameterName.optional().describe("Name of the length parameter (default 'Length')"),
  diameterParameter: parameterName.optional().describe("Name of the diameter parameter (default 'Diameter')"),
  depthParameter: parameterName.optional().describe("Name of the depth parameter (default 'Depth')"),
  templatePath: z.string().min(1).max(400).optional().describe("Explicit .rft template path (default found in the Revit template folders)"),
  saveFolder: z
    .string()
    .min(1)
    .max(400)
    .optional()
    .describe("Folder for the generated .rfa (default %APPDATA%\\revit-mcp\\families) - build an office library this way"),
  overwrite: z.boolean().optional().describe("Rebuild and reload when a family with this name is already in the project"),
  types: z.array(typeItem).max(500).optional().describe("Types to create or update after loading"),
};

const schemaObject = z.object(createFamilySchema).strict();
export type CreateFamilyArgs = z.infer<typeof schemaObject>;

export function validateCreateFamily(args: CreateFamilyArgs): string | null {
  const circular = args.shape === "circular";
  for (const type of args.types ?? []) {
    if (circular && (type.width !== undefined || type.length !== undefined))
      return `Type '${type.name}': circular families take diameter, not width/length`;
    if (!circular && type.diameter !== undefined) return `Type '${type.name}': rectangular families take width/length, not diameter`;
  }
  if (circular && (args.width !== undefined || args.length !== undefined)) return "Circular families take diameter, not width/length";
  if (!circular && args.diameter !== undefined) return "Rectangular families take width/length, not diameter";
  return null;
}

export function registerCreateFamilyTool(server: McpServer) {
  server.tool(
    "create_family",
    "Build a parametric family (.rfa) from a Revit family template when no suitable family exists, load it and create sized types - e.g. a rectangular barrette pile (Width x Length x Depth) or a round bored pile (Diameter x Depth). " +
      "The plan profile is locked to reference planes driven by labelled dimensions (symmetric about the centre planes) and the extrusion end is driven by Depth, so types flex like a hand-made family; a flex test runs before saving and is reported. " +
      "Foundation families extrude down from the insertion point (insertion point = pile top), so place them with create_foundations kind:'isolated' and topOffset, or kind:'pile' with length. " +
      "The .rfa is saved to saveFolder (default %APPDATA%\\revit-mcp\\families) for reuse via load_family. Existing family with the same name is reused unless overwrite:true. All lengths mm.",
    createFamilySchema,
    async (args) => {
      const error = validateCreateFamily(args as CreateFamilyArgs);
      if (error) return { content: [{ type: "text" as const, text: `create_family failed: ${error}` }], isError: true };
      return sendDocumentationCommand("create_family", args, 330000);
    }
  );
}
