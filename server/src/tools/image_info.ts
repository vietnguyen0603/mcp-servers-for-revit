import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { readPngHeader, requireFile } from "../utils/imageRaster.js";
import { errorResult, imagePathSchema } from "../utils/imageToolSchemas.js";

/** Common sheet sizes in inches (long x short). */
export const STANDARD_SHEETS: Array<{ name: string; long: number; short: number }> = [
  { name: "ARCH E1 30x42", long: 42, short: 30 },
  { name: "ARCH E 36x48", long: 48, short: 36 },
  { name: "ARCH D 24x36", long: 36, short: 24 },
  { name: "ANSI D 22x34", long: 34, short: 22 },
  { name: "ARCH C 18x24", long: 24, short: 18 },
  { name: "ANSI B 11x17", long: 17, short: 11 },
  { name: "Letter 8.5x11", long: 11, short: 8.5 },
  { name: "ISO A0-A4 (sqrt 2)", long: 46.81, short: 33.11 },
];

const round = (n: number, d = 2) => Math.round(n * 10 ** d) / 10 ** d;

export function sheetSuggestions(width: number, height: number, sheetWidthIn?: number) {
  if (sheetWidthIn) {
    const pxPerInch = width / sheetWidthIn;
    return { pxPerInch: round(pxPerInch), sheetWidthIn, impliedSheetHeightIn: round(height / pxPerInch) };
  }
  const landscape = width >= height;
  const aspect = Math.max(width, height) / Math.min(width, height);
  const candidates = STANDARD_SHEETS.filter((s) => Math.abs(s.long / s.short - aspect) / aspect < 0.015).map((s) => {
    const sheetW = landscape ? s.long : s.short;
    return { sheet: s.name, sheetWidthIn: sheetW, pxPerInch: round(width / sheetW) };
  });
  return { aspectRatio: round(aspect, 4), candidates };
}

export function registerImageInfoTool(server: McpServer) {
  server.tool(
    "image_info",
    "Read the pixel size of a local PNG (header only, no decode) and, for sheet exports, the pixels per sheet inch: exact when sheetWidthIn is given, otherwise suggested from standard sheet sizes matching the aspect ratio. No Revit connection needed.",
    {
      imagePath: imagePathSchema.describe("Path of the PNG"),
      sheetWidthIn: z
        .number()
        .positive()
        .max(500)
        .optional()
        .describe("Sheet width in inches along the image width (e.g. 42 for a landscape 30x42 sheet)"),
    },
    async (args) => {
      try {
        const header = await readPngHeader(args.imagePath);
        const { size } = await requireFile(args.imagePath);
        const info = {
          imagePath: args.imagePath,
          width: header.width,
          height: header.height,
          bitDepth: header.bitDepth,
          colorType: header.colorType,
          fileBytes: size,
          decodedRgbaMB: round((header.width * header.height * 4) / 1048576, 1),
          sheet: sheetSuggestions(header.width, header.height, args.sheetWidthIn),
        };
        return { content: [{ type: "text" as const, text: JSON.stringify(info, null, 2) }] };
      } catch (error) {
        return errorResult("image_info", error);
      }
    }
  );
}
