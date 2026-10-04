import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import {
  classRowReader,
  classifyOverlay,
  darkMask,
  downscale,
  encodePng,
  fitScale,
  loadPng,
  overlayStats,
  readPngHeader,
  resolveRegion,
  savePng,
} from "../utils/imageRaster.js";
import { errorResult, imagePathSchema, pixelRegionSchema, pngSavePathSchema } from "../utils/imageToolSchemas.js";

export function registerOverlayImagesTool(server: McpServer) {
  server.tool(
    "overlay_images",
    "Overlay a rebuilt sheet/view export on the original export (same pixel size) to spot misplaced or missing linework. Returns a PNG where original-only dark pixels are red, rebuilt-only blue and coincident black, plus the % of dark pixels matched / original-only / rebuilt-only overall and per named region (ranked worst first). No Revit connection needed.",
    {
      originalPath: imagePathSchema.describe("PNG of the original (reference) drawing"),
      rebuiltPath: imagePathSchema.describe("PNG of the rebuilt drawing, same width/height as the original"),
      region: pixelRegionSchema
        .optional()
        .describe("Compare only this rectangle (image px, x1/y1 exclusive); default whole image"),
      regions: z
        .array(
          z.object({
            name: z.string().min(1).max(100),
            x0: z.number().int().min(0),
            y0: z.number().int().min(0),
            x1: z.number().int().min(1),
            y1: z.number().int().min(1),
          })
        )
        .max(200)
        .optional()
        .describe("Named rectangles in image px (e.g. one per detail) to score separately; clipped to region"),
      threshold: z
        .number()
        .int()
        .min(1)
        .max(255)
        .optional()
        .default(200)
        .describe("A pixel is dark when its luminance is below this (0-255, alpha composited on white)"),
      tolerancePx: z
        .number()
        .int()
        .min(0)
        .max(10)
        .optional()
        .default(0)
        .describe("Count a dark pixel as matched if the other image has a dark pixel within this many px (0 = exact)"),
      maxOutputPx: z.number().int().min(200).max(4000).optional().default(1750).describe("Longest side of the returned image"),
      savePath: pngSavePathSchema.optional().describe("Also write the overlay PNG here (absolute path)"),
    },
    async (args) => {
      try {
        const [origHeader, rebuiltHeader] = await Promise.all([
          readPngHeader(args.originalPath).catch((e) => {
            throw new Error(`original: ${e instanceof Error ? e.message : String(e)}`);
          }),
          readPngHeader(args.rebuiltPath).catch((e) => {
            throw new Error(`rebuilt: ${e instanceof Error ? e.message : String(e)}`);
          }),
        ]);
        if (origHeader.width !== rebuiltHeader.width || origHeader.height !== rebuiltHeader.height) {
          throw new Error(
            `image sizes differ: original ${origHeader.width}x${origHeader.height}, rebuilt ${rebuiltHeader.width}x${rebuiltHeader.height}. Export both with the same pixelSize and fitDirection.`
          );
        }
        const { width, height } = origHeader;
        const region = resolveRegion(width, height, args.region);

        // Decode one image at a time and keep only its dark mask for the region.
        const originalMask = darkMask(await loadPng(args.originalPath, "original"), region, args.threshold);
        const rebuiltMask = darkMask(await loadPng(args.rebuiltPath, "rebuilt"), region, args.threshold);
        const classes = classifyOverlay(originalMask, rebuiltMask, region.width, region.height, args.tolerancePx);

        const { scale, outWidth, outHeight } = fitScale(region.width, region.height, args.maxOutputPx);
        const rgba = downscale(
          region.width,
          region.height,
          classRowReader(classes, region.width),
          outWidth,
          outHeight,
          "ink"
        );
        const png = encodePng(outWidth, outHeight, rgba);
        if (args.savePath) await savePng(args.savePath, png);

        const summary: Record<string, unknown> = {
          imageSize: { width, height },
          regionOrigin: { x: region.x, y: region.y },
          regionSize: { width: region.width, height: region.height },
          scaleApplied: Math.round(scale * 10000) / 10000,
          outputSize: { width: outWidth, height: outHeight },
          threshold: args.threshold,
          tolerancePx: args.tolerancePx,
          legend: "red = original only, blue = rebuilt only, black = both; percentages are of all dark pixels (union)",
          overall: overlayStats(classes, region.width, region.height),
        };
        if (args.regions?.length) {
          summary.regions = args.regions
            .map((r) => {
              const rect = {
                x: Math.min(r.x0, r.x1) - region.x,
                y: Math.min(r.y0, r.y1) - region.y,
                width: Math.abs(r.x1 - r.x0),
                height: Math.abs(r.y1 - r.y0),
              };
              return { name: r.name, ...overlayStats(classes, region.width, region.height, rect) };
            })
            .sort((a, b) => a.matchedPct - b.matchedPct || b.darkPixels - a.darkPixels);
        }
        if (args.savePath) summary.savedTo = args.savePath;

        return {
          content: [
            { type: "image" as const, data: png.toString("base64"), mimeType: "image/png" },
            { type: "text" as const, text: JSON.stringify(summary, null, 2) },
          ],
        };
      } catch (error) {
        return errorResult("overlay_images", error);
      }
    }
  );
}
