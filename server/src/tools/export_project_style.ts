import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

export const PROJECT_STYLE_SECTIONS = [
  "linePatterns",
  "fillPatterns",
  "lineStyles",
  "objectStyles",
  "arrowheads",
  "textTypes",
  "dimensionTypes",
  "gridTypes",
  "levelTypes",
  "viewportTypes",
  "viewFilters",
  "viewTemplates",
  "tagFamilies",
  "titleBlocks",
  "annotationSymbols",
  "otherAnnotationFamilies",
  "defaultTypes",
  "materials",
  "wallTypes",
  "floorTypes",
] as const;

export const exportProjectStyleSchema = {
  sourceDocument: z.string().min(1).max(256).optional().describe("Title of another open project to export instead of the active one"),
  sourcePath: z
    .string()
    .min(1)
    .max(400)
    .optional()
    .describe("Path of a .rvt/.rte to export; opened in the background (detached, worksets closed) and closed without saving"),
  outFile: z
    .string()
    .min(1)
    .max(400)
    .optional()
    .describe("JSON file to write (on the Revit PC), e.g. 'C:/Standards/office-style.json'. Default: %TEMP%/revit-mcp/<project>-style.json"),
  sections: z
    .array(z.enum(PROJECT_STYLE_SECTIONS))
    .min(1)
    .max(PROJECT_STYLE_SECTIONS.length)
    .optional()
    .describe("Sections to export. Default: everything except materials, wallTypes and floorTypes (ask for those explicitly)"),
  includeParameters: z
    .boolean()
    .optional()
    .describe("Also dump every writable type / template parameter by name (default true; false gives a smaller file)"),
  returnProfile: z.boolean().optional().describe("Also return the whole profile in the response (can be large; default false)"),
};

export function registerExportProjectStyleTool(server: McpServer) {
  server.tool(
    "export_project_style",
    "Capture a project's whole drawing style as a JSON profile file that can be re-applied to another project: view templates (scale, detail level, discipline, view range, category overrides, filters with overrides, controlled parameters), " +
      "view filters (categories + rules), text/dimension/spot/grid/level/viewport types with key values, arrowheads, line styles, line patterns (segments), fill patterns (grids), object styles, " +
      "tag families per category, title blocks, annotation symbols and default types. Values use tool units: mm, degrees, [r,g,b], pen weights 1-16, names for patterns/types. " +
      "Read-only. Exports the active document unless sourceDocument/sourcePath is given. Returns the file path and a compact summary; the schema is documented in docs/project-style-profile.md.",
    exportProjectStyleSchema,
    async (args) => {
      if (args.sourceDocument && args.sourcePath) {
        return { content: [{ type: "text" as const, text: "Give at most one of sourceDocument or sourcePath." }], isError: true };
      }
      return sendDocumentationCommand("export_project_style", args, 600000);
    }
  );
}
