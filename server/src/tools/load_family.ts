import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

const familyItem = z
  .object({
    path: z.string().min(1).max(400).optional().describe("Full path of the .rfa file"),
    name: z
      .string()
      .min(1)
      .max(256)
      .optional()
      .describe("Family file name to search for in the Revit libraries, e.g. 'M_WWF-Welded Wide Flange-Column' or 'Pile Cap 4 Pile'"),
    pathContains: z
      .string()
      .min(1)
      .max(256)
      .optional()
      .describe("Disambiguate library matches, e.g. 'Structural Columns' or 'English\\\\US'"),
    exact: z.boolean().optional().describe("searchOnly: match the whole file name instead of all words"),
  })
  .strict()
  .refine((f) => f.path !== undefined || f.name !== undefined, { message: "Give path or name" });

export function registerLoadFamilyTool(server: McpServer) {
  server.tool(
    "load_family",
    "Load Revit families (.rfa) into the active project by full path or by name searched in the Revit library folders (Options > File Locations plus C:\\ProgramData\\Autodesk\\RVT <version>\\Libraries), and return each family with its types (typeId, name) so they can be placed or duplicated with create_family_type. " +
      "With searchOnly:true nothing is loaded; matching file paths are returned instead (words of the name must all appear in the file name unless exact). Families already in the project are returned without reloading unless overwrite is true. One undo step.",
    {
      families: z.array(familyItem).min(1).max(100).describe("Families to load or search"),
      searchOnly: z.boolean().optional().describe("Only search the libraries and list matching files"),
      overwrite: z.boolean().optional().describe("Reload families that are already in the project (overwrites parameter values)"),
      libraryRoots: z.array(z.string().min(1).max(400)).max(20).optional().describe("Extra folders to search first"),
    },
    async (args) => sendDocumentationCommand("load_family", args)
  );
}
