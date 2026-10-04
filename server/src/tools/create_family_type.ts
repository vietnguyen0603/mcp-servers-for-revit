import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

const parameterValue = z.union([z.number(), z.string().max(1024), z.boolean()]);

const typeItem = z
  .object({
    newName: z.string().min(1).max(256).describe("Name of the type to create (or update if it exists)"),
    sourceTypeId: z.number().int().positive().optional().describe("Type to duplicate"),
    familyName: z
      .string()
      .min(1)
      .max(256)
      .optional()
      .describe("Loaded family to duplicate a type of, e.g. 'M_WWF-Welded Wide Flange-Column' (see load_family)"),
    typeName: z.string().min(1).max(256).optional().describe("Which type of familyName/category to duplicate (default the first)"),
    category: z
      .string()
      .min(1)
      .max(128)
      .optional()
      .describe("For system types without a family: OST_Floors, OST_Walls, OST_Roofs, OST_Ceilings, OST_StructuralFoundation, ..."),
    parameters: z
      .record(z.string().min(1).max(256), parameterValue)
      .optional()
      .describe(
        "Type parameters to set. Numbers on length parameters are millimetres (e.g. {d:1000, bf:1000, tw:20, tf:20} for a welded I, {b:600, h:800} for a concrete beam, {Width:4000, Length:5500, 'Foundation Thickness':1500} for a footing); strings use Revit's display format"
      ),
    thickness: z
      .number()
      .positive()
      .optional()
      .describe("Wall/floor/roof/ceiling types: total thickness in mm (the core layer absorbs the change)"),
    ifExists: z
      .enum(["update", "reuse", "error"])
      .optional()
      .describe("When newName already exists: update its parameters (default), reuse it unchanged, or fail"),
    listParameters: z.boolean().optional().describe("Return the type's editable parameters and values"),
  })
  .strict()
  .refine((t) => t.sourceTypeId !== undefined || t.familyName !== undefined || t.category !== undefined, {
    message: "Give sourceTypeId, familyName or category",
  });

export function registerCreateFamilyTypeTool(server: McpServer) {
  server.tool(
    "create_family_type",
    "Create sized types by duplicating an existing family or system type and setting its type parameters - the step every structural model needs before placing elements: welded/rolled steel sections (WWF: d, bf, tw, tf), concrete columns and beams (b, h), footings/pile caps (Width, Length, Foundation Thickness), piles, and floor/wall/roof thickness (thickness, mm). " +
      "Lengths given as numbers are millimetres. Re-running with the same newName updates the existing type (idempotent). Each item reports typeId, applied parameters and warnings (unknown parameter names come back with the list of the type's parameters). Load the family first with load_family. One undo step.",
    {
      types: z.array(typeItem).min(1).max(500).describe("Types to create or update"),
    },
    async (args) => sendDocumentationCommand("create_family_type", args)
  );
}
