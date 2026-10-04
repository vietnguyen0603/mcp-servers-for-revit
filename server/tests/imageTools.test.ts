import { mkdtempSync, readFileSync, writeFileSync } from "fs";
import { tmpdir } from "os";
import path from "path";
import { PNG } from "pngjs";
import { afterEach, describe, expect, it } from "vitest";
import { registerCropImageGridTool } from "../src/tools/crop_image_grid.js";
import { registerImageInfoTool, sheetSuggestions } from "../src/tools/image_info.js";
import { registerOverlayImagesTool } from "../src/tools/overlay_images.js";
import {
  CLASS_MATCHED,
  CLASS_ORIGINAL_ONLY,
  CLASS_REBUILT_ONLY,
  classifyOverlay,
  clearImageCache,
  computeGridLayout,
  downscale,
  drawText,
  fitScale,
  measureText,
  overlayStats,
  resolveRegion,
  rgbaRegionReader,
} from "../src/utils/imageRaster.js";
import { createFakeMcpServer } from "./helpers/fakeMcpServer.js";

type ImageResult = {
  content: Array<{ type: string; text?: string; data?: string; mimeType?: string }>;
  isError?: boolean;
};

const dir = mkdtempSync(path.join(tmpdir(), "image-tools-"));

/** Write a white PNG with the given black pixels (or rectangles). */
function writePng(name: string, width: number, height: number, dark: Array<[number, number, number?, number?]>) {
  const png = new PNG({ width, height });
  png.data.fill(255);
  for (const [x, y, w = 1, h = 1] of dark) {
    for (let yy = y; yy < y + h; yy++) {
      for (let xx = x; xx < x + w; xx++) {
        const o = (yy * width + xx) * 4;
        png.data[o] = png.data[o + 1] = png.data[o + 2] = 0;
      }
    }
  }
  const file = path.join(dir, name);
  writeFileSync(file, PNG.sync.write(png));
  return file;
}

function decode(result: ImageResult) {
  const image = result.content.find((c) => c.type === "image");
  return PNG.sync.read(Buffer.from(image!.data!, "base64"));
}

function summary(result: ImageResult) {
  return JSON.parse(result.content.find((c) => c.type === "text")!.text!);
}

function tools() {
  const { server, getTool } = createFakeMcpServer();
  registerCropImageGridTool(server);
  registerOverlayImagesTool(server);
  registerImageInfoTool(server);
  return getTool;
}

afterEach(() => clearImageCache());

describe("crop math", () => {
  it("clamps and normalises regions", () => {
    expect(resolveRegion(100, 80)).toEqual({ x: 0, y: 0, width: 100, height: 80 });
    expect(resolveRegion(100, 80, { x0: 10, y0: 20, x1: 60, y1: 50 })).toEqual({ x: 10, y: 20, width: 50, height: 30 });
    expect(resolveRegion(100, 80, { x0: 90, y0: 70, x1: 500, y1: 500 })).toEqual({ x: 90, y: 70, width: 10, height: 10 });
    expect(() => resolveRegion(100, 80, { x0: 200, y0: 0, x1: 300, y1: 10 })).toThrow(/does not overlap/);
  });

  it("fits the longer side and never enlarges", () => {
    expect(fitScale(3200, 1600, 1600)).toEqual({ scale: 0.5, outWidth: 1600, outHeight: 800 });
    expect(fitScale(400, 300, 1600)).toEqual({ scale: 1, outWidth: 400, outHeight: 300 });
  });

  it("crops the requested pixels at 1:1", () => {
    const png = new PNG({ width: 4, height: 3 });
    for (let i = 0; i < 12; i++) png.data.set([i * 10, i * 10, i * 10, 255], i * 4);
    const region = resolveRegion(4, 3, { x0: 1, y0: 1, x1: 3, y1: 3 });
    const out = downscale(2, 2, rgbaRegionReader(png, region), 2, 2, "box");
    expect([out[0], out[4], out[8], out[12]]).toEqual([50, 60, 90, 100]);
  });

  it("ink downscaling keeps a 1 px line dark while box fades it", () => {
    const png = new PNG({ width: 4, height: 4 });
    png.data.fill(255);
    for (let x = 0; x < 4; x++) png.data.set([0, 0, 0, 255], (1 * 4 + x) * 4); // row 1 black
    const read = rgbaRegionReader(png, resolveRegion(4, 4));
    expect(downscale(4, 4, read, 2, 2, "box")[0]).toBe(127);
    expect(downscale(4, 4, read, 2, 2, "ink")[0]).toBe(0);
  });
});

describe("grid layout", () => {
  it("places lines at scaled original coordinates and labels original values", () => {
    const layout = computeGridLayout(1000, 500, 500, 250, 50, 2);
    expect(layout.vertical).toHaveLength(21);
    expect(layout.horizontal).toHaveLength(11);
    // 4-digit labels need >50 output px, so only every 2nd major line (every 200 px) is labelled
    expect(layout.xLabelStep).toBe(200);
    expect(layout.vertical.find((l) => l.value === 300)!.major).toBe(true);
    expect(layout.vertical.find((l) => l.value === 300)!.label).toBeUndefined();
    const v400 = layout.vertical.find((l) => l.value === 400)!;
    expect(v400.pos).toBe(layout.offsetX + 200);
    expect(v400.label?.text).toBe("400");
    expect(layout.vertical.find((l) => l.value === 350)!.major).toBe(false);
    const h200 = layout.horizontal.find((l) => l.value === 200)!;
    expect(h200.pos).toBe(layout.offsetY + 100);
    expect(h200.label?.text).toBe("200");
    // y labels are right-aligned in the left margin, x labels sit in the top margin
    expect(h200.label!.x + measureText("200", 2).width).toBeLessThanOrEqual(layout.offsetX);
    expect(v400.label!.y + measureText("400", 2).height).toBeLessThanOrEqual(layout.offsetY);
    // label centred on its line
    expect(Math.abs(v400.label!.x + measureText("400", 2).width / 2 - v400.pos)).toBeLessThanOrEqual(1);
  });

  it("thins labels instead of overlapping when heavily downscaled", () => {
    const layout = computeGridLayout(7000, 5000, 1600, 1143, 50, 2);
    expect(layout.xLabelStep).toBeGreaterThan(100);
    const labelled = layout.vertical.filter((l) => l.label);
    for (let i = 1; i < labelled.length; i++) {
      const prev = labelled[i - 1].label!;
      expect(labelled[i].label!.x).toBeGreaterThan(prev.x + measureText(prev.text, 2).width);
    }
    expect(labelled.every((l) => l.value % layout.xLabelStep === 0)).toBe(true);
  });
});

describe("digit font", () => {
  it("renders inside the measured bounds and fills them", () => {
    const w = 80;
    const h = 30;
    const buf = Buffer.alloc(w * h * 4, 255);
    const text = "-1890";
    drawText(buf, w, h, 3, 4, text, 2, [0, 0, 0]);
    const size = measureText(text, 2);
    expect(size).toEqual({ width: (5 * 5 + 4) * 2, height: 14 });
    let minX = w, minY = h, maxX = -1, maxY = -1;
    for (let y = 0; y < h; y++)
      for (let x = 0; x < w; x++)
        if (buf[(y * w + x) * 4] === 0) {
          minX = Math.min(minX, x);
          maxX = Math.max(maxX, x);
          minY = Math.min(minY, y);
          maxY = Math.max(maxY, y);
        }
    expect(minX).toBe(3); // '-' starts at its left edge
    expect(maxX).toBe(3 + size.width - 1); // '0' reaches its right edge
    expect(minY).toBe(4);
    expect(maxY).toBe(4 + size.height - 1);
  });

  it("clips at the canvas edge without throwing", () => {
    const buf = Buffer.alloc(10 * 10 * 4, 255);
    expect(() => drawText(buf, 10, 10, -5, 5, "88", 2, [0, 0, 0])).not.toThrow();
  });
});

describe("overlay classification", () => {
  it("classifies matched / original-only / rebuilt-only and honours tolerance", () => {
    const orig = Uint8Array.from([1, 1, 0, 0, 0, 0]);
    const rebuilt = Uint8Array.from([1, 0, 1, 0, 0, 1]);
    const exact = classifyOverlay(orig, rebuilt, 6, 1, 0);
    expect(Array.from(exact)).toEqual([CLASS_MATCHED, CLASS_ORIGINAL_ONLY, CLASS_REBUILT_ONLY, 0, 0, CLASS_REBUILT_ONLY]);
    const stats = overlayStats(exact, 6, 1);
    expect(stats).toMatchObject({ darkPixels: 4, matched: 1, originalOnly: 1, rebuiltOnly: 2, matchedPct: 25 });
    const loose = classifyOverlay(orig, rebuilt, 6, 1, 1);
    expect(Array.from(loose)).toEqual([CLASS_MATCHED, CLASS_MATCHED, CLASS_MATCHED, 0, 0, CLASS_REBUILT_ONLY]);
  });
});

describe("tools", () => {
  it("crop_image_grid returns a gridded PNG with crop info", async () => {
    const file = writePng("detail.png", 400, 300, [[100, 100, 200, 2]]);
    const result = (await tools()("crop_image_grid").invoke({
      imagePath: file,
      region: { x0: 50, y0: 60, x1: 350, y1: 260 },
      sheetOrigin: { x: 1000, y: 2000 },
    })) as ImageResult;
    expect(result.isError).toBeUndefined();
    const info = summary(result);
    expect(info.cropOrigin).toEqual({ x: 50, y: 60 });
    expect(info.cropSize).toEqual({ width: 300, height: 200 });
    expect(info.scaleApplied).toBe(1);
    expect(info.cropOriginInSheet).toEqual({ x: 1050, y: 2060 });
    const png = decode(result);
    expect([png.width, png.height]).toEqual([info.outputSize.width, info.outputSize.height]);
    // the black line (image y=100 -> crop y=40) is preserved on top of the grid
    const { x: ox, y: oy } = info.drawingOffsetInOutput;
    const o = ((oy + 40) * png.width + ox + 100) * 4;
    expect([png.data[o], png.data[o + 1], png.data[o + 2]]).toEqual([0, 0, 0]);
  });

  it("downscales to maxOutputPx and saves the PNG", async () => {
    const file = writePng("big.png", 2000, 1000, []);
    const savePath = path.join(dir, "out", "grid.png");
    const result = (await tools()("crop_image_grid").invoke({ imagePath: file, maxOutputPx: 500, savePath })) as ImageResult;
    const info = summary(result);
    expect(info.scaleApplied).toBe(0.25);
    expect(PNG.sync.read(readFileSync(savePath)).width).toBe(info.outputSize.width);
  });

  it("rejects missing files and non-PNG files", async () => {
    const missing = (await tools()("crop_image_grid").invoke({ imagePath: path.join(dir, "nope.png") })) as ImageResult;
    expect(missing.isError).toBe(true);
    expect(missing.content[0].text).toMatch(/not found/);
    const txt = path.join(dir, "fake.png");
    writeFileSync(txt, "hello world, definitely not a png file at all");
    const bad = (await tools()("image_info").invoke({ imagePath: txt })) as ImageResult;
    expect(bad.isError).toBe(true);
    expect(bad.content[0].text).toMatch(/not a PNG/);
  });

  it("overlay_images colours and scores differences per region", async () => {
    const original = writePng("orig.png", 100, 50, [[10, 10, 20, 1], [60, 10, 20, 1]]);
    const rebuilt = writePng("rebuilt.png", 100, 50, [[10, 10, 20, 1], [60, 20, 20, 1]]);
    const result = (await tools()("overlay_images").invoke({
      originalPath: original,
      rebuiltPath: rebuilt,
      regions: [
        { name: "left", x0: 0, y0: 0, x1: 50, y1: 50 },
        { name: "right", x0: 50, y0: 0, x1: 100, y1: 50 },
      ],
    })) as ImageResult;
    expect(result.isError).toBeUndefined();
    const info = summary(result);
    expect(info.overall).toMatchObject({ darkPixels: 60, matched: 20, originalOnly: 20, rebuiltOnly: 20 });
    expect(info.regions.map((r: { name: string }) => r.name)).toEqual(["right", "left"]);
    expect(info.regions[1].matchedPct).toBe(100);
    const png = decode(result);
    const px = (x: number, y: number) => Array.from(png.data.subarray((y * 100 + x) * 4, (y * 100 + x) * 4 + 3));
    expect(px(15, 10)).toEqual([0, 0, 0]);
    expect(px(65, 10)).toEqual([230, 0, 0]);
    expect(px(65, 20)).toEqual([0, 70, 255]);
    expect(px(5, 5)).toEqual([255, 255, 255]);
  });

  it("overlay_images rejects mismatched sizes", async () => {
    const a = writePng("a.png", 10, 10, []);
    const b = writePng("b.png", 12, 10, []);
    const result = (await tools()("overlay_images").invoke({ originalPath: a, rebuiltPath: b })) as ImageResult;
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toMatch(/sizes differ/);
  });

  it("image_info reports size and sheet px-per-inch", async () => {
    const file = writePng("sheet.png", 700, 500, []);
    const info = summary((await tools()("image_info").invoke({ imagePath: file })) as ImageResult);
    expect(info).toMatchObject({ width: 700, height: 500 });
    expect(info.sheet.candidates[0]).toMatchObject({ sheet: "ARCH E1 30x42", sheetWidthIn: 42 });
    expect(sheetSuggestions(7000, 5000, 42)).toEqual({ pxPerInch: 166.67, sheetWidthIn: 42, impliedSheetHeightIn: 30 });
  });

  it("validates input", () => {
    const crop = tools()("crop_image_grid");
    expect(() => crop.parse({ imagePath: "x.png", region: { x0: 10, y0: 0, x1: 5, y1: 10 } })).toThrow();
    expect(() => crop.parse({ imagePath: "x.png", savePath: "relative.png" })).toThrow(/absolute/);
    expect(crop.parse({ imagePath: "x.png" })).toMatchObject({ gridStep: 50, labelEvery: 2, maxOutputPx: 1600 });
  });
});
