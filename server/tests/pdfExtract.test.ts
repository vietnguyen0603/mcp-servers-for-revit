import { describe, it, expect, beforeAll } from "vitest";
import fs from "fs";
import os from "os";
import path from "path";
import {
  clipLineToBox,
  mergeCollinear,
  minAreaRect,
  simplifyClosed,
  polygonArea,
  type Pt,
} from "../src/utils/pdf/geometry.js";
import { Mask, boundaryLoops, connectedComponents, fillPolygon, floodOutside } from "../src/utils/pdf/raster.js";
import { fitGridCalibration, fitLine, toModel, toPage, twoPointTransform, type GridLinePage } from "../src/utils/pdf/calibration.js";
import { flattenPath, interpretOperatorList, type Mat } from "../src/utils/pdf/pdfPage.js";
import { decompose } from "../src/utils/pdf/cuts.js";
import { findXs } from "../src/utils/pdf/slabs.js";
import { runExtract } from "../src/utils/pdf/extract.js";
import { pdfExtractSchema } from "../src/tools/pdf_extract.js";
import { z } from "zod";

// ---------------------------------------------------------------- geometry

describe("pdf geometry", () => {
  it("merges nearly collinear dashes far from the origin into one run", () => {
    // dashed diagonal at ~68 deg near (31000, 1200) with float noise on the middle dash
    const segs = [
      { a: [31150, 1197] as Pt, b: [31349, 1697] as Pt },
      { a: [31484, 2031] as Pt, b: [31619, 2364] as Pt },
      { a: [31753, 2698] as Pt, b: [31952, 3198] as Pt },
    ];
    const lines = mergeCollinear(segs, { distTol: 30, angTol: (1.5 * Math.PI) / 180, gap: 800 });
    expect(lines).toHaveLength(1);
    expect(lines[0].runs).toHaveLength(1);
    const [s0, s1] = lines[0].runs[0];
    expect(s1 - s0).toBeCloseTo(Math.hypot(802, 2001), -1);
  });

  it("keeps parallel lines apart and splits runs at big gaps", () => {
    const segs = [
      { a: [0, 0] as Pt, b: [100, 0] as Pt },
      { a: [150, 0] as Pt, b: [250, 0] as Pt },
      { a: [1000, 0] as Pt, b: [1100, 0] as Pt },
      { a: [0, 50] as Pt, b: [100, 50] as Pt },
    ];
    const lines = mergeCollinear(segs, { distTol: 5, angTol: 0.01, gap: 60 });
    // far-apart collinear pieces may live in separate Line objects; their runs are what matters
    expect(lines.filter((l) => Math.abs(Math.abs(l.o) - 50) < 1)).toHaveLength(1);
    const runs = lines
      .filter((l) => Math.abs(l.o) < 1)
      .flatMap((l) => l.runs)
      .sort((p, q) => p[0] - q[0]);
    expect(runs).toEqual([
      [0, 250],
      [1000, 1100],
    ]);
  });

  it("fits a rotated minimum-area rectangle", () => {
    const a = (30 * Math.PI) / 180;
    const u: Pt = [Math.cos(a), Math.sin(a)];
    const v: Pt = [-u[1], u[0]];
    const pts: Pt[] = [];
    for (const [s, t] of [
      [-800, -500],
      [800, -500],
      [800, 500],
      [-800, 500],
    ])
      pts.push([1000 + s * u[0] + t * v[0], 2000 + s * u[1] + t * v[1]]);
    const r = minAreaRect(pts);
    expect(Math.max(r.w, r.h)).toBeCloseTo(1600, 6);
    expect(Math.min(r.w, r.h)).toBeCloseTo(1000, 6);
    expect(r.center[0]).toBeCloseTo(1000, 6);
    expect(r.center[1]).toBeCloseTo(2000, 6);
  });

  it("simplifies a staircase ring and clips lines to a box", () => {
    const ring: Pt[] = [];
    for (let i = 0; i <= 10; i++) ring.push([i, 0]);
    for (let i = 1; i <= 10; i++) ring.push([10, i]);
    for (let i = 9; i >= 0; i--) ring.push([i, 10]);
    for (let i = 9; i >= 1; i--) ring.push([0, i]);
    expect(simplifyClosed(ring, 0.5)).toHaveLength(4);
    const c = clipLineToBox([5, 5], [1, 0], [0, 0, 10, 10])!;
    expect(c[0]).toEqual([0, 5]);
    expect(c[1]).toEqual([10, 5]);
    expect(clipLineToBox([5, 20], [1, 0], [0, 0, 10, 10])).toBeNull();
  });

  it("fits a line through segments (total least squares)", () => {
    const ang = (15.129 * Math.PI) / 180;
    const u: Pt = [Math.cos(ang), Math.sin(ang)];
    const segs = [0, 30, 60, 90].map((s) => ({ a: [s * u[0], s * u[1]] as Pt, b: [(s + 20) * u[0], (s + 20) * u[1]] as Pt }));
    const f = fitLine(segs)!;
    expect(Math.abs(f.u[0] * u[1] - f.u[1] * u[0])).toBeLessThan(1e-9);
  });
});

// ---------------------------------------------------------------- raster

describe("pdf raster", () => {
  it("fills polygons with even-odd holes and traces outer + hole loops", () => {
    const m = new Mask(20, 20);
    fillPolygon(
      m,
      [
        [
          [2, 2],
          [18, 2],
          [18, 18],
          [2, 18],
        ],
        [
          [6, 6],
          [14, 6],
          [14, 14],
          [6, 14],
        ],
      ],
      true
    );
    expect(m.count()).toBe(16 * 16 - 8 * 8);
    const cc = connectedComponents(m);
    expect(cc.count).toBe(1);
    const loops = boundaryLoops(cc.labels, m.w, m.h);
    const outer = loops.filter((l) => l.area > 0);
    const holes = loops.filter((l) => l.area < 0);
    expect(outer).toHaveLength(1);
    expect(outer[0].area).toBe(256);
    expect(holes).toHaveLength(1);
    expect(holes[0].area).toBe(-64);
    expect(outer[0].pts).toHaveLength(4);
  });

  it("treats diagonal pixels as one 8-connected loop", () => {
    const m = new Mask(4, 4);
    m.data[1 * 4 + 1] = 1;
    m.data[2 * 4 + 2] = 1;
    const cc = connectedComponents(m, true);
    expect(cc.count).toBe(1);
    const loops = boundaryLoops(cc.labels, 4, 4);
    expect(loops).toHaveLength(1);
    expect(loops[0].area).toBe(2);
  });

  it("flood fills the outside of a closed outline", () => {
    const m = new Mask(10, 10);
    for (let i = 2; i <= 7; i++) {
      m.data[2 * 10 + i] = 1;
      m.data[7 * 10 + i] = 1;
      m.data[i * 10 + 2] = 1;
      m.data[i * 10 + 7] = 1;
    }
    const out = floodOutside(m);
    expect(out.get(0, 0)).toBe(1);
    expect(out.get(5, 5)).toBe(0);
    expect(out.get(2, 2)).toBe(0);
  });
});

// ---------------------------------------------------------------- calibration

describe("pdf calibration", () => {
  it("maps two points with scale, flip and rotation", () => {
    const t = twoPointTransform([100, 300], [0, 0], [300, 300], [10000, 0]);
    expect(toModel(t, [100, 100])).toEqual([expect.closeTo(0, 6), expect.closeTo(10000, 6)]);
    const back = toPage(t, [5000, 5000]);
    expect(back[0]).toBeCloseTo(200, 6);
    expect(back[1]).toBeCloseTo(200, 6);
  });

  it("fits scale and offset from grid lines with residuals", () => {
    const line = (label: string, p: Pt, u: Pt): GridLinePage => ({ label, bubbles: [], p, u, s0: 0, s1: 100, diameter: 20 });
    const lines = [
      line("A", [100, 50], [0, 1]),
      line("B", [300, 50], [0, 1]),
      line("1", [50, 300], [1, 0]),
      line("2", [50, 100], [1, 0]),
    ];
    const c = fitGridCalibration(lines, { x: { A: 0, B: 10000 }, y: { 1: 0, 2: 10000 } });
    expect(c.mmPerPoint).toBeCloseTo(50, 9);
    expect(toModel(c.transform, [300, 100])).toEqual([expect.closeTo(10000, 6), expect.closeTo(10000, 6)]);
    expect(c.residuals!.every((r) => Math.abs(r.error) < 0.01)).toBe(true);
    expect(() => fitGridCalibration(lines, { x: { A: 0, Z: 5 }, y: { 1: 0 } })).toThrow(/Z/);
  });
});

// ---------------------------------------------------------------- operator list interpretation

describe("pdf operator interpretation", () => {
  const OPS = {
    save: 10,
    restore: 11,
    transform: 12,
    setLineWidth: 2,
    setFillRGBColor: 59,
    setStrokeRGBColor: 58,
    constructPath: 91,
    clip: 40,
    eoClip: 41,
    fill: 22,
    stroke: 20,
    endPath: 28,
    eoFill: 23,
    fillStroke: 24,
    eoFillStroke: 25,
    closeStroke: 21,
    closeFillStroke: 26,
    closeEOFillStroke: 27,
    setDash: 3,
    paintFormXObjectBegin: 74,
    paintFormXObjectEnd: 75,
    setFillColorN: 55,
    setStrokeColorN: 54,
    shadingFill: 62,
  };
  const rect = (x: number, y: number, w: number, h: number) => new Float32Array([0, x, y, 1, x + w, y, 1, x + w, y + h, 1, x, y + h, 4]);

  it("tracks the CTM, line width, colours and clip paths", () => {
    const base: Mat = [1, 0, 0, -1, 0, 100];
    const fn = [OPS.save, OPS.transform, OPS.setLineWidth, OPS.eoClip, OPS.constructPath, OPS.setFillRGBColor, OPS.constructPath, OPS.restore, OPS.constructPath];
    const args = [
      null,
      [0.5, 0, 0, 0.5, 0, 0],
      [2],
      null,
      [OPS.endPath, [rect(0, 0, 10, 10)], null],
      ["#c0c0c0"],
      [OPS.fill, [rect(0, 0, 40, 40)], null],
      null,
      [OPS.stroke, [rect(0, 0, 4, 4)], null],
    ];
    const paths = interpretOperatorList(fn, args, OPS, base);
    expect(paths).toHaveLength(2);
    const [fill, stroke] = paths;
    expect(fill.fill).toBe(true);
    expect(fill.fillColor).toEqual([192, 192, 192]);
    expect(fill.clips).toHaveLength(1);
    expect(fill.clips[0].evenOdd).toBe(true);
    expect(fill.clips[0].bbox).toEqual([0, 95, 5, 100]);
    expect(fill.bbox).toEqual([0, 80, 20, 100]);
    expect(stroke.clips).toHaveLength(0);
    expect(stroke.lineWidth).toBe(1); // restored CTM -> width 1 at scale 1
  });

  it("flattens bezier curves and counts pieces", () => {
    const k = 0.5523;
    const data = new Float32Array([0, 10, 0, 2, 10, k * 10, k * 10, 10, 0, 10, 1, -10, 10, 4]);
    const sp = flattenPath(data, [1, 0, 0, 1, 0, 0]);
    expect(sp).toHaveLength(1);
    expect(sp[0].curves).toBe(1);
    expect(sp[0].closed).toBe(true);
    expect(sp[0].pts.slice(-2)).toEqual([10, 0]);
  });
});

// ---------------------------------------------------------------- walls / openings

describe("pdf cut decomposition and X openings", () => {
  it("pairs opposite edges of an L-shaped cut into two walls", () => {
    // L: horizontal leg 0..5000 x 0..400, vertical leg 0..400 x 0..3000
    const poly: Pt[] = [
      [0, 0],
      [5000, 0],
      [5000, 400],
      [400, 400],
      [400, 3000],
      [0, 3000],
    ];
    const inside = (p: Pt) => (p[0] >= 0 && p[0] <= 5000 && p[1] >= 0 && p[1] <= 400) || (p[0] >= 0 && p[0] <= 400 && p[1] >= 0 && p[1] <= 3000);
    const walls = decompose(poly, [], inside);
    expect(walls).toHaveLength(2);
    expect(walls.every((w) => w.thickness === 400)).toBe(true);
    const h = walls.find((w) => w.start.y === w.end.y)!;
    expect(h.start.y).toBe(200);
    // extended to the centre line of the vertical wall
    expect(Math.min(h.start.x, h.end.x)).toBe(200);
    expect(Math.max(h.start.x, h.end.x)).toBe(5000);
  });

  it("detects a dashed X even when a diagonal continues past the corner", () => {
    const dashes = (a: Pt, b: Pt, n: number) => {
      const out: { a: Pt; b: Pt }[] = [];
      for (let i = 0; i < n; i++) {
        const t0 = i / n;
        const t1 = (i + 0.6) / n;
        out.push({ a: [a[0] + (b[0] - a[0]) * t0, a[1] + (b[1] - a[1]) * t0], b: [a[0] + (b[0] - a[0]) * t1, a[1] + (b[1] - a[1]) * t1] });
      }
      // last dash ends exactly on the corner
      out.push({ a: [a[0] + (b[0] - a[0]) * 0.95, a[1] + (b[1] - a[1]) * 0.95], b });
      return out;
    };
    const segs = [...dashes([30000, 1000], [31000, 3000], 5), ...dashes([30000, 3000], [31000, 1000], 5)];
    // an unrelated collinear piece just beyond one corner
    segs.push({ a: [31100, 3200], b: [31300, 3600] });
    const lines = mergeCollinear(segs, { distTol: 30, angTol: (1.5 * Math.PI) / 180, gap: 800 });
    const xs = findXs(lines, 600);
    expect(xs).toHaveLength(1);
    const q = xs[0];
    expect(Math.abs(polygonArea(q))).toBeCloseTo(2e6, -4);
  });
});

// ---------------------------------------------------------------- end to end with a synthetic PDF

/** Build a minimal one-page PDF with Helvetica as /F1. */
function makePdf(content: string, w: number, h: number): Buffer {
  const objs = [
    "<< /Type /Catalog /Pages 2 0 R >>",
    "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${w} ${h}] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>`,
    `<< /Length ${Buffer.byteLength(content)} >>\nstream\n${content}\nendstream`,
    "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
  ];
  let out = "%PDF-1.4\n";
  const offsets: number[] = [];
  objs.forEach((o, i) => {
    offsets.push(Buffer.byteLength(out));
    out += `${i + 1} 0 obj\n${o}\nendobj\n`;
  });
  const xref = Buffer.byteLength(out);
  out += `xref\n0 ${objs.length + 1}\n0000000000 65535 f \n`;
  for (const off of offsets) out += `${String(off).padStart(10, "0")} 00000 n \n`;
  out += `trailer\n<< /Size ${objs.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(out, "latin1");
}

const H = 400; // page height; helpers take top-down y like the extractor
const Y = (yd: number) => H - yd;
const f = (n: number) => n.toFixed(3);

function circle(cx: number, cyd: number, r: number) {
  const k = 0.5523 * r;
  const cy = Y(cyd);
  return (
    `${f(cx + r)} ${f(cy)} m ` +
    `${f(cx + r)} ${f(cy + k)} ${f(cx + k)} ${f(cy + r)} ${f(cx)} ${f(cy + r)} c ` +
    `${f(cx - k)} ${f(cy + r)} ${f(cx - r)} ${f(cy + k)} ${f(cx - r)} ${f(cy)} c ` +
    `${f(cx - r)} ${f(cy - k)} ${f(cx - k)} ${f(cy - r)} ${f(cx)} ${f(cy - r)} c ` +
    `${f(cx + k)} ${f(cy - r)} ${f(cx + r)} ${f(cy - k)} ${f(cx + r)} ${f(cy)} c S\n`
  );
}
function text(s: string, x: number, yd: number, size: number, angDeg = 0) {
  const a = (angDeg * Math.PI) / 180;
  return `BT /F1 ${size} Tf ${f(Math.cos(a))} ${f(Math.sin(a))} ${f(-Math.sin(a))} ${f(Math.cos(a))} ${f(x)} ${f(Y(yd))} Tm (${s}) Tj ET\n`;
}
function dashed(x0: number, y0d: number, x1: number, y1d: number, dash = 8, gap = 3) {
  const L = Math.hypot(x1 - x0, y1d - y0d);
  const ux = (x1 - x0) / L;
  const uy = (y1d - y0d) / L;
  let s = "";
  for (let t = 0; t < L; t += dash + gap) {
    const e = Math.min(L, t + dash);
    s += `${f(x0 + ux * t)} ${f(Y(y0d + uy * t))} m ${f(x0 + ux * e)} ${f(Y(y0d + uy * e))} l S\n`;
  }
  return s;
}
function line(x0: number, y0d: number, x1: number, y1d: number) {
  return `${f(x0)} ${f(Y(y0d))} m ${f(x1)} ${f(Y(y1d))} l S\n`;
}

/**
 * Plan at 50 mm per point: grids A (x=100), B (x=300), 1 (y=300 down), 2 (y=100 down).
 * Model origin = A/1.
 */
function syntheticPlan(): string {
  let s = "0.24 w\n";
  // grid lines + bubbles
  s += dashed(100, 70, 100, 330) + dashed(300, 70, 300, 330) + dashed(70, 300, 330, 300) + dashed(70, 100, 330, 100);
  s += "0.5 w\n" + circle(100, 60, 10) + circle(300, 60, 10) + circle(60, 300, 10) + circle(60, 100, 10);
  s += text("A", 96.5, 64, 10) + text("B", 296.5, 64, 10) + text("1", 57, 304, 10) + text("2", 57, 104, 10);
  // column C1 1000x1000 at A/1 (gray fill + outline)
  s += `0.75 g 90 ${Y(310)} 20 20 re f\n0.96 w 0 G 90 ${Y(310)} 20 20 re S\n`;
  s += text("C1", 112, 320, 4);
  // rotated column C2 1000x1600 at B/2, rotated 30 deg: axis-aligned fill clipped by the rotated rectangle
  const a = (30 * Math.PI) / 180;
  const corners = [
    [-10, -16],
    [10, -16],
    [10, 16],
    [-10, 16],
  ].map(([x, y]) => [300 + x * Math.cos(a) - y * Math.sin(a), Y(100) + x * Math.sin(a) + y * Math.cos(a)]);
  const poly = corners.map((c, i) => `${f(c[0])} ${f(c[1])} ${i ? "l" : "m"}`).join(" ") + " h";
  s += `q ${poly} W n 0.75 g 270 ${Y(130)} 60 60 re f Q\n`;
  s += `0.96 w ${poly} S\n`;
  s += text("C2", 315, 85, 4);
  // wall 400 thick along y = 5000 from x = 2500..7500
  s += `0.75 g 150 ${Y(204)} 100 8 re f\n`;
  // beam B1-300x600 between parallel lines at y = 2500 (+-150), x = 1000..9000
  s += "0.3 w 0 G\n" + line(120, 247, 280, 247) + line(120, 253, 280, 253);
  s += text("B1-300x600", 185, 251.2, 3);
  // slab edge
  s += `0.7 w 80 ${Y(320)} 240 240 re S\n`;
  // opening with a dashed X: x 2500..4500, y 8000..9000 -> page x 150..190, y 120..140 (down)
  s += "0.24 w\n" + dashed(150, 120, 190, 140, 6, 2) + dashed(150, 140, 190, 120, 6, 2);
  // barrette 2800x1200 drawn as four lines: model x 5000..7800, y 6300..7500
  s += "0.5 w\n" + line(200, 150, 256, 150) + line(256, 150, 256, 174) + line(256, 174, 200, 174) + line(200, 174, 200, 150);
  s += text("P1", 225, 182, 4);
  return s;
}

describe("pdf_extract end to end (synthetic PDF)", () => {
  let file = "";
  beforeAll(() => {
    file = path.join(os.tmpdir(), `pdf_extract_test_${process.pid}.pdf`);
    fs.writeFileSync(file, makePdf(syntheticPlan(), 600, H));
  });

  it("calibrates from grid bubbles and extracts everything", async () => {
    const r: any = await runExtract({
      path: file,
      page: 1,
      calibration: { grids: { x: { A: 0, B: 10000 }, y: { 1: 0, 2: 10000 } } },
      extract: ["grids", "text", "cuts", "beams", "slabOutline", "openings", "rects"],
      options: { text: { pattern: "^C\\d" }, rects: { sizes: [[2800, 1200]], labelPattern: "^P\\d" } },
    });
    expect(r.calibration.mmPerPoint).toBeCloseTo(50, 1);
    for (const res of r.calibration.residuals) expect(Math.abs(res.error)).toBeLessThan(2);

    const byLabel = Object.fromEntries(r.grids.map((g: any) => [g.label, g]));
    expect(byLabel.A.orientation).toBe("x");
    expect(Math.abs(byLabel.A.value)).toBeLessThanOrEqual(1);
    expect(byLabel.B.value).toBeCloseTo(10000, -1);
    expect(byLabel["2"].orientation).toBe("y");
    expect(byLabel["2"].value).toBeCloseTo(10000, -1);

    expect(r.text.map((t: any) => t.text).sort()).toEqual(["C1", "C2"]);

    const cols = r.cuts.columns;
    expect(cols).toHaveLength(2);
    const c1 = cols.find((c: any) => c.mark === "C1");
    expect(Math.abs(c1.x)).toBeLessThan(15);
    expect(Math.abs(c1.y)).toBeLessThan(15);
    expect(c1.b).toBeCloseTo(1000, -2);
    expect(c1.h).toBeCloseTo(1000, -2);
    const c2 = cols.find((c: any) => c.mark === "C2");
    expect(c2.x).toBeCloseTo(10000, -2);
    expect(c2.y).toBeCloseTo(10000, -2);
    expect([c2.b, c2.h].sort((p: number, q: number) => p - q)).toEqual([expect.closeTo(1000, -2), expect.closeTo(1600, -2)]);
    // rotation is reported in [-45, 45): 30 deg or -60 deg -> 30
    expect(Math.abs(c2.rotationDeg - 30)).toBeLessThan(1);

    expect(r.cuts.walls).toHaveLength(1);
    const w = r.cuts.walls[0];
    expect(w.thickness).toBe(400);
    expect(w.start.y).toBeCloseTo(5000, -1);
    expect(Math.min(w.start.x, w.end.x)).toBeCloseTo(2500, -1);
    expect(Math.max(w.start.x, w.end.x)).toBeCloseTo(7500, -1);

    const b = r.beams.beams;
    expect(b).toHaveLength(1);
    expect(b[0]).toMatchObject({ name: "B1", W: 300, H: 600 });
    expect(b[0].start.x).toBeCloseTo(1000, -1);
    expect(b[0].end.x).toBeCloseTo(9000, -1);
    expect(b[0].start.y).toBeCloseTo(2500, -1);

    expect(r.slabOutline.areaM2).toBeGreaterThan(142);
    expect(r.slabOutline.areaM2).toBeLessThan(146);
    expect(r.slabOutline.outline).toHaveLength(4);

    expect(r.openings.openings).toHaveLength(1);
    const ob = r.openings.openings[0].bbox;
    expect(ob.minX).toBeCloseTo(2500, -2);
    expect(ob.maxX).toBeCloseTo(4500, -2);
    expect(ob.minY).toBeCloseTo(8000, -2);
    expect(ob.maxY).toBeCloseTo(9000, -2);

    expect(r.rects.rects).toHaveLength(1);
    expect(r.rects.rects[0]).toMatchObject({ x: 6400, y: 6900, length: 2800, width: 1200, rotationDeg: 0, mark: "P1" });
  });

  it("returns page coordinates for grids without calibration and rejects modes needing one", async () => {
    const r: any = await runExtract({ path: file, page: 1, extract: ["grids"] });
    expect(r.calibration.units).toBe("pt");
    expect(r.grids.find((g: any) => g.label === "A").value).toBeCloseTo(100, 0);
    await expect(runExtract({ path: file, page: 1, extract: ["cuts"] })).rejects.toThrow(/calibration/);
    await expect(runExtract({ path: file, page: 2, extract: ["grids"] })).rejects.toThrow(/out of range/);
  });

  it("validates input with zod", () => {
    const schema = z.object(pdfExtractSchema);
    expect(schema.safeParse({ path: "relative.pdf", page: 1, extract: ["grids"] }).success).toBe(false);
    expect(schema.safeParse({ path: "C:/a.pdf", page: 0, extract: ["grids"] }).success).toBe(false);
    expect(schema.safeParse({ path: "C:/a.pdf", page: 1, extract: [] }).success).toBe(false);
    expect(schema.safeParse({ path: "C:/a.pdf", page: 1, extract: ["walls"] }).success).toBe(false);
    expect(
      schema.safeParse({ path: "C:/a.pdf", page: 1, extract: ["cuts"], calibration: { grids: { x: { A2: 0, E2: 54400 }, y: { "5": 0 } } } }).success
    ).toBe(true);
    expect(schema.safeParse({ path: "C:/a.pdf", page: 1, extract: ["grids"], outFile: "C:/out.txt" }).success).toBe(false);
  });
});

// ---------------------------------------------------------------- real drawings (skipped when absent)

const NEXUS_SUP = "E:/VNguyen/RnD/revit/ai-modeling/NEXUS B-STR-SUP-CD DRAWINGs_231124.pdf";
const NEXUS_SUB = "E:/VNguyen/RnD/revit/ai-modeling/NEXUS B-STR-SUB-CD DRAWINGs_231124.pdf";
const hasNexus = fs.existsSync(NEXUS_SUP) && fs.existsSync(NEXUS_SUB);
const NEXUS_CAL = { grids: { x: { A2: 0, E2: 54400 }, y: { "5": 0, "2": 28000 } } };

describe.skipIf(!hasNexus)("pdf_extract on the Nexus drawings", () => {
  it("sup p29: grids, scale and columns", async () => {
    const r: any = await runExtract({ path: NEXUS_SUP, page: 29, calibration: NEXUS_CAL, extract: ["grids", "cuts"] });
    expect(r.calibration.mmPerPoint).toBeCloseTo(53.44, 1);
    const g = Object.fromEntries(r.grids.map((x: any) => [x.label, x]));
    for (const [k, v] of Object.entries({ A2: 0, B2: 6150, C2: 14650, D2: 46150, E2: 54400 })) expect(Math.abs(g[k].value - v)).toBeLessThan(10);
    for (const [k, v] of Object.entries({ "5": 0, "6": 2830, "4": 4000, "7": 13530, "3": 16000, "2": 28000 })) expect(Math.abs(g[k].value - v)).toBeLessThan(10);
    const c = g.C;
    const xAt0 = c.start.x + ((0 - c.start.y) * (c.end.x - c.start.x)) / (c.end.y - c.start.y);
    expect(Math.abs(xAt0 - 20330)).toBeLessThan(30);
    expect(Math.abs(Math.abs(c.angleDeg) - (90 - 15.129))).toBeLessThan(0.05);
    const c2 = r.cuts.columns.find((x: any) => x.mark === "C2" && Math.abs(x.y - 28000) < 100);
    expect(Math.abs(c2.x - 550)).toBeLessThan(20);
    expect(c2.b).toBeGreaterThan(1080);
    const c4 = r.cuts.columns.filter((x: any) => /^C4/.test(x.mark ?? ""));
    expect(c4.length).toBe(5);
    for (const x of c4) expect(Math.abs(x.rotationDeg + 15.13)).toBeLessThan(0.3);
    const thick = new Set(r.cuts.walls.map((w: any) => Math.round(w.thickness / 100) * 100));
    expect([...thick].sort()).toEqual(expect.arrayContaining([400, 500, 600]));
  }, 60000);

  it("sup p20: beams, slab outline and openings", async () => {
    const r: any = await runExtract({ path: NEXUS_SUP, page: 20, calibration: NEXUS_CAL, extract: ["beams", "slabOutline", "openings"] });
    const hb6 = r.beams.beams.filter((b: any) => b.name === "L5-18.HB6");
    expect(hb6).toHaveLength(1);
    expect(hb6[0]).toMatchObject({ W: 2000, H: 450 });
    expect(Math.abs(r.slabOutline.areaM2 - 1355)).toBeLessThan(10);
    expect(r.openings.openings.length).toBeGreaterThanOrEqual(12);
  }, 60000);

  it("sub p38: diaphragm wall ring 800 and 1800", async () => {
    const r: any = await runExtract({ path: NEXUS_SUB, page: 38, calibration: NEXUS_CAL, extract: ["cuts"] });
    const t = r.cuts.walls.map((w: any) => w.thickness);
    expect(t.filter((x: number) => x === 800).length).toBeGreaterThanOrEqual(4);
    expect(t.filter((x: number) => Math.abs(x - 1800) <= 10).length).toBeGreaterThanOrEqual(2);
  }, 60000);

  it("sub p18: barrette piles", async () => {
    const r: any = await runExtract({
      path: NEXUS_SUB,
      page: 18,
      calibration: NEXUS_CAL,
      extract: ["rects"],
      options: { rects: { sizes: [[2800, 1200], [2800, 1500]], labelPattern: "^(SGBR-\\w+(\\.\\d+)?|TP\\d)$" } },
    });
    expect(r.rects.rects.length).toBeGreaterThanOrEqual(26);
    expect(r.rects.rects.every((p: any) => p.mark)).toBe(true);
  }, 60000);
});
