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
    folder: z
      .string()
      .min(1)
      .max(400)
      .optional()
      .describe("Load every .rfa in this folder (office library folder); backup files *.0001.rfa are skipped"),
    pattern: z.string().min(1).max(256).optional().describe("folder: file name filter, e.g. 'M_Pile*' (default all)"),
    recursive: z.boolean().optional().describe("folder: include sub-folders"),
    types: z
      .array(z.string().min(1).max(256))
      .max(200)
      .optional()
      .describe("Load only these types (families with large type catalogs); adds missing types to a family already in the project"),
    pathContains: z
      .string()
      .min(1)
      .max(256)
      .optional()
      .describe("Disambiguate library matches, e.g. 'Structural Columns' or 'English\\\\US'"),
    exact: z.boolean().optional().describe("searchOnly: match the whole file name instead of all words"),
  })
  .strict()
  .refine((f) => [f.path, f.name, f.folder].filter((v) => v !== undefined).length === 1, {
    message: "Give exactly one of path, name or folder",
  });

export function registerLoadFamilyTool(server: McpServer) {
  server.tool(
    "load_family",
    "Load Revit families (.rfa) into the active project by full path, by name searched in the Revit library folders (Options > File Locations plus C:\\ProgramData\\Autodesk\\RVT <version>\\Libraries), or every .rfa of a folder (pattern, recursive), and return each family with its types (typeId, name) so they can be placed or duplicated with create_family_type. " +
      "types loads only selected types (and adds missing types to a loaded family). With searchOnly:true nothing is loaded; matching file paths are returned instead (words of the name must all appear in the file name unless exact). " +
      "Families already in the project are returned without reloading unless overwrite is true. To take families from another project instead of .rfa files use copy_families. One undo step.",
    {
      families: z.array(familyItem).min(1).max(100).describe("Families to load or search"),
      searchOnly: z.boolean().optional().describe("Only search the libraries and list matching files"),
      overwrite: z.boolean().optional().describe("Reload families that are already in the project (overwrites parameter values)"),
      libraryRoots: z.array(z.string().min(1).max(400)).max(20).optional().describe("Extra folders to search first"),
    },
    async (args) => sendDocumentationCommand("load_family", args, 600000)
  );
}
