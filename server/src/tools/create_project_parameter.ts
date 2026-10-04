import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";
import { categoryNameSchema } from "../utils/elementFilterSchemas.js";

export const projectParameterDataTypes = ["text", "length", "integer", "number", "yesno", "material"] as const;

export const projectParameterGroups = [
  "identityData",
  "text",
  "constraints",
  "dimensions",
  "data",
  "general",
  "construction",
  "structural",
  "materials",
  "graphics",
  "phasing",
  "other",
] as const;

/** Characters Revit refuses in parameter names. */
const FORBIDDEN_NAME_CHARS = /[\\:{}[\]|;<>?`~]/;

export const parameterNameSchema = z
  .string()
  .trim()
  .min(1)
  .max(128)
  .refine((name) => !FORBIDDEN_NAME_CHARS.test(name), {
    message: "Parameter names cannot contain \\ : { } [ ] | ; < > ? ` ~",
  });

export const projectParameterDefinitionSchema = z
  .object({
    name: parameterNameSchema.describe('Parameter name, e.g. "Tag Text" or "Beam Label"'),
    dataType: z
      .enum(projectParameterDataTypes)
      .optional()
      .default("text")
      .describe("Data type (default text). Use text for composed tag labels such as 'L5-18.HB6-2000x450'"),
    categories: z
      .array(categoryNameSchema)
      .min(1)
      .max(50)
      .describe('Categories to bind to, e.g. ["StructuralFraming","StructuralColumns","StructuralFoundation"]'),
    binding: z
      .enum(["instance", "type"])
      .optional()
      .default("instance")
      .describe("instance (a value per element, default) or type (one value per family type)"),
    group: z
      .enum(projectParameterGroups)
      .optional()
      .describe("Properties-palette group (default identityData for new parameters; an existing parameter keeps its group unless given)"),
    sharedGroup: z
      .string()
      .trim()
      .min(1)
      .max(128)
      .optional()
      .describe("Group inside the shared parameter file (default 'revit-mcp')"),
    description: z.string().max(1024).optional().describe("Tooltip text stored with a new shared definition"),
    varyByGroup: z
      .boolean()
      .optional()
      .describe("Instance parameters only: let values vary between model group instances"),
    guid: z
      .string()
      .uuid()
      .optional()
      .describe("Explicit GUID for a new shared definition (default: a GUID derived from the name, so re-runs and other projects get the same one)"),
  })
  .strict()
  .refine((p) => p.varyByGroup === undefined || p.binding !== "type", {
    message: "varyByGroup applies to instance parameters only",
  });

export const createProjectParameterShape = {
  action: z
    .enum(["create", "list"])
    .optional()
    .describe("create (default when parameters are given) or list (default otherwise): list every project parameter with its binding, categories, group, data type and shared GUID"),
  parameters: z
    .array(projectParameterDefinitionSchema)
    .min(1)
    .max(50)
    .optional()
    .describe("Parameters to create or extend"),
  sharedParameterFile: z
    .string()
    .min(1)
    .max(1024)
    .optional()
    .describe("Absolute path of the shared parameter file to use/create (default %APPDATA%\\revit-mcp\\shared-parameters.txt). Revit's own shared parameter file setting is restored afterwards"),
};

const createProjectParameterSchema = z.object(createProjectParameterShape);
export type CreateProjectParameterArgs = z.infer<typeof createProjectParameterSchema>;

export function registerCreateProjectParameterTool(server: McpServer) {
  server.tool(
    "create_project_parameter",
    "Create shared project parameters and bind them to categories (instance or type), or list the existing project parameters. " +
      "Each definition is written to a shared parameter file (default %APPDATA%\\revit-mcp\\shared-parameters.txt, group 'revit-mcp') with a GUID derived from the name, then bound to the categories. " +
      "Idempotent: an existing parameter of that name keeps its definition and binding kind and only gains the missing categories (status created / updated / unchanged). " +
      "Typical use: a text parameter such as 'Tag Text' on Structural Framing/Columns/Foundations, filled by set_parameters with an expression like '{Mark}-{b}x{h}', then shown by a tag type that displays it (see probe_tag_types) - the API cannot add labels to tag families.",
    createProjectParameterShape,
    async (args) => {
      const action = args.action ?? (args.parameters?.length ? "create" : "list");
      if (action === "create" && !args.parameters?.length) {
        return {
          content: [{ type: "text" as const, text: "create_project_parameter: give parameters to create, or action:'list'." }],
          isError: true,
        };
      }
      return sendDocumentationCommand("create_project_parameter", { ...args, action }, 120000);
    }
  );
}
