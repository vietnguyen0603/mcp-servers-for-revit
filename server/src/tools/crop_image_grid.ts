import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import {
  computeGridLayout,
  downscale,
  encodePng,
  fitScale,
  loadPng,
  renderGridCanvas,
  resolveRegion,
  rgbaRegionReader,
  savePng,
} from "../utils/imageRaster.js";
import { errorResult, imagePathSchema, pixelRegionSchema, pngSavePathSchema } from "../utils/imageToolSchemas.js";

export function registerCropImageGridTool(server: McpServer) {
  server.tool(
    "crop_image_grid",
    "Crop a local PNG (e.g. a sheet exported with export_view_image) and return it as an image with a labelled pixel grid, so exact coordinates can be read off a detail. Grid lines every gridStep px, darker and numbered every labelEvery lines (x labels along the top, y labels down the left); the drawing stays on top of the grid. Labels always show ORIGINAL crop-local pixel coordinates even when the output is downscaled to fit maxOutputPx; image px = cropOrigin + label. No Revit connection needed.",
    {
      imagePath: imagePathSchema.describe("Path of the PNG to read"),
      region: pixelRegionSchema
        .optional()
        .describe("Crop rectangle in image pixels {x0,y0,x1,y1} (x1/y1 exclusive, clamped); default whole image"),
      gridStep: z.number().int().min(5).max(5000).optional().default(50).describe("Grid spacing in original pixels"),
      labelEvery: z
        .number()
        .int()
        .min(1)
        .max(100)
        .optional()
        .default(2)
        .describe("Every n-th grid line is darker and labelled (default 2 -> labels every 100 px)"),
      maxOutputPx: z
        .number()
        .int()
        .min(200)
        .max(4000)
        .optional()
        .default(1600)
        .describe("Longest side of the drawn crop in output pixels (crops are downscaled, never enlarged)"),
      resample: z
        .enum(["ink", "box", "nearest"])
        .optional()
        .default("ink")
        .describe("Downscale filter: ink = area average that keeps thin lines dark (default), box = plain average, nearest"),
      sheetOrigin: z
        .object({ x: z.number(), y: z.number() })
        .optional()
        .describe("If imagePath is itself a cut-out of a larger sheet image, its top-left in sheet px; echoed so labels map back to sheet px"),
      savePath: pngSavePathSchema.optional().describe("Also write the gridded PNG here (absolute path)"),
    },
    async (args) => {
      try {
        const image = await loadPng(args.imagePath);
        const region = resolveRegion(image.width, image.height, args.region);
        const { scale, outWidth, outHeight } = fitScale(region.width, region.height, args.maxOutputPx);
        const scaled = downscale(
          region.width,
          region.height,
          rgbaRegionReader(image, region),
          outWidth,
          outHeight,
          args.resample
        );
        const layout = computeGridLayout(region.width, region.height, outWidth, outHeight, args.gridStep, args.labelEvery);
        const canvas = renderGridCanvas(scaled, layout);
        const png = encodePng(layout.canvasWidth, layout.canvasHeight, canvas);
        if (args.savePath) await savePng(args.savePath, png);

        const summary: Record<string, unknown> = {
          imagePath: args.imagePath,
          imageSize: { width: image.width, height: image.height },
          cropOrigin: { x: region.x, y: region.y },
          cropSize: { width: region.width, height: region.height },
          scaleApplied: Math.round(scale * 10000) / 10000,
          outputSize: { width: layout.canvasWidth, height: layout.canvasHeight },
          drawingOffsetInOutput: { x: layout.offsetX, y: layout.offsetY },
          gridStep: args.gridStep,
          labelStep: { x: layout.xLabelStep, y: layout.yLabelStep },
          labels:
            "Labels are ORIGINAL crop-local pixels (the grid is drawn after scaling at the scaled positions of the original coordinates). imageX = cropOrigin.x + labelX, imageY = cropOrigin.y + labelY.",
        };
        if (layout.xLabelStep !== args.gridStep * args.labelEvery || layout.yLabelStep !== args.gridStep * args.labelEvery) {
          summary.note = "Some major lines are unlabelled because labels would overlap at this scale; zoom into a smaller region for denser labels.";
        }
        if (args.gridStep * scale < 4) {
          summary.warning = `Grid lines are only ${(args.gridStep * scale).toFixed(1)} output px apart; increase gridStep or crop a smaller region.`;
        }
        if (args.sheetOrigin) {
          summary.sheetOrigin = args.sheetOrigin;
          summary.cropOriginInSheet = { x: args.sheetOrigin.x + region.x, y: args.sheetOrigin.y + region.y };
          summary.sheetMapping = "sheetX = cropOriginInSheet.x + labelX, sheetY = cropOriginInSheet.y + labelY";
        }
        if (args.savePath) summary.savedTo = args.savePath;

        return {
          content: [
            { type: "image" as const, data: png.toString("base64"), mimeType: "image/png" },
            { type: "text" as const, text: JSON.stringify(summary, null, 2) },
          ],
        };
      } catch (error) {
        return errorResult("crop_image_grid", error);
      }
    }
  );
}
