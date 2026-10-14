/**
 * Packing for layout_detail_sheet_grid: the office "relative 12-cell" detail
 * sheet grid. Pure math on the measurements returned by the Revit command
 * (sheet millimetres); the resulting plan is sent back to Revit to apply.
 *
 * Rules:
 *  - cols x rows logical cells inside the title block's inner border, numbered
 *    1.. from the top-left, left to right then down. A detail's number is the
 *    cell holding its title, i.e. the bottom-left cell of its block.
 *  - Cells are relative: a small detail keeps the nominal cell size, a wide or
 *    tall one stretches its columns/rows and pushes the next ones along.
 *    Leftover space goes to the right / bottom. Rows have their own column
 *    widths unless a detail spans them.
 *  - Details fill from cell 1; minimise sheets per series, then sheets holding
 *    a single small detail, then blank space inside the blocks.
 *  - Title at the bottom-left of the block, aligned per row; title line =
 *    max(one nominal cell, drawing width, title text), capped at the block;
 *    the drawing is centred over the line, so it starts at the title's left edge.
 *  - No-Title viewports overlapping a titled one are its sub-views (composite)
 *    and are numbered <n>A, <n>B ...
 */

export type Box = [number, number, number, number];

export interface MeasuredViewport {
  viewportId: number;
  viewId: number;
  viewName?: string;
  typeId: number;
  titled: boolean;
  detailNumber: string;
  /** Viewport box outline without the title (x0, y0, x1, y1). */
  box: Box;
  /** Title outline width with a 1 mm title line (number + text), null if unknown. */
  titleWidth?: number | null;
  /** Label outline min - (box min + LabelOffset); null before Revit 2022. */
  labelK?: [number, number] | null;
  /** Label outline width - LabelLineLength; negative/null when not measurable. */
  labelExtra?: number | null;
  labelHeight?: number | null;
}

export interface MeasuredSheet {
  id: number;
  number: string;
  name: string;
  viewports: MeasuredViewport[];
}

export interface Border {
  x0: number;
  y0: number;
  x1: number;
  y1: number;
}

export interface GridOptions {
  cols: number;
  rows: number;
  pad: number;
  gap: number;
  tries: number;
}

export interface SeriesInput {
  prefix: string;
  start?: number;
  digits?: number;
  name?: string;
  sheetNumbers?: string[];
}

export interface PlannedViewport {
  viewportId: number;
  viewId: number;
  typeId: number;
  center: { x: number; y: number };
  detailNumber: string;
  labelOffset?: { x: number; y: number };
  labelLineLength?: number;
  old: { sheetNumber: string; detailNumber: string };
}

export interface PlannedSheet {
  number: string;
  name: string;
  /** Existing selected sheet that keeps this number, if any. */
  sheetId?: number;
  /** Sheet whose title block and parameters a new sheet copies. */
  templateSheetId: number;
  occupancy: string[];
  details: string[];
  viewports: PlannedViewport[];
}

export interface GridPlan {
  sheets: PlannedSheet[];
  /** Selected sheets that end up empty (deleted by apply when allowed). */
  emptiedSheets: { id: number; number: string }[];
  /** "detail/sheet" before -> after, only for viewports whose reference changes. */
  oldToNew: Record<string, string>;
  errors: string[];
}

interface Vp extends MeasuredViewport {
  sheet: MeasuredSheet;
}

interface Unit {
  vs: Vp[];
  main: Vp;
  box: Box;
  w: number;
  h: number;
}

interface Item {
  r: number;
  c: number;
  ch: number;
  cw: number;
  u: Unit;
}

interface PackSheet {
  occ: boolean[][];
  items: Item[];
}

interface LabelConstants {
  kx: number | null;
  ky: number | null;
  extra: number;
  h: number;
}

const DEFAULT_LABEL_EXTRA = 12.7;
const DEFAULT_LABEL_HEIGHT = 16.4;

const round2 = (v: number) => Math.round(v * 100) / 100;
const sum = (xs: number[], from = 0, to = xs.length) => {
  let s = 0;
  for (let i = from; i < to; i++) s += xs[i];
  return s;
};

/** Upper median, as the original script. */
function median(values: Array<number | null | undefined>, fallback: number | null): number | null {
  const xs = values.filter((v): v is number => typeof v === "number" && Number.isFinite(v)).sort((a, b) => a - b);
  return xs.length ? xs[Math.floor(xs.length / 2)] : fallback;
}

function overlap(a: Box, b: Box): boolean {
  return Math.min(a[2], b[2]) - Math.max(a[0], b[0]) > 0.5 && Math.min(a[3], b[3]) - Math.max(a[1], b[1]) > 0.5;
}

function intersection(a: Box, b: Box): number {
  return (Math.min(a[2], b[2]) - Math.max(a[0], b[0])) * (Math.min(a[3], b[3]) - Math.max(a[1], b[1]));
}

/** Deterministic PRNG (mulberry32) so plan and apply produce the same layout. */
function rng(seed: number) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** Title geometry per viewport type: median of the measured values. */
export function labelConstants(sheets: MeasuredSheet[]): Map<number, LabelConstants> {
  const byType = new Map<number, MeasuredViewport[]>();
  for (const s of sheets)
    for (const v of s.viewports)
      if (v.titled) byType.set(v.typeId, [...(byType.get(v.typeId) ?? []), v]);
  const out = new Map<number, LabelConstants>();
  for (const [type, vs] of byType) {
    out.set(type, {
      kx: median(vs.map((v) => v.labelK?.[0]), null),
      ky: median(vs.map((v) => v.labelK?.[1]), null),
      extra: median(vs.map((v) => (v.labelExtra != null && v.labelExtra >= 0 ? v.labelExtra : null)), DEFAULT_LABEL_EXTRA)!,
      h: median(vs.map((v) => v.labelHeight), DEFAULT_LABEL_HEIGHT)!,
    });
  }
  return out;
}

/** Composite detection: each No-Title viewport joins the titled one it overlaps most on its sheet. */
export function buildUnits(sheets: MeasuredSheet[], labels: Map<number, LabelConstants>, pad: number, gap: number) {
  const units: Unit[] = [];
  for (const sheet of sheets) {
    const vs: Vp[] = sheet.viewports.map((v) => ({ ...v, sheet }));
    const mains = vs.filter((v) => v.titled);
    const groups = new Map<Vp, Vp[]>(mains.map((m) => [m, [m]]));
    for (const v of vs) {
      if (v.titled) continue;
      const hits = mains.filter((m) => overlap(m.box, v.box));
      if (hits.length) {
        const best = hits.reduce((a, b) => (intersection(b.box, v.box) > intersection(a.box, v.box) ? b : a));
        groups.get(best)!.push(v);
      } else {
        groups.set(v, [v]); // stand-alone notes block / schedule gets a number of its own
      }
    }
    for (const [main, g] of groups) {
      const box: Box = [
        Math.min(...g.map((v) => v.box[0])),
        Math.min(...g.map((v) => v.box[1])),
        Math.max(...g.map((v) => v.box[2])),
        Math.max(...g.map((v) => v.box[3])),
      ];
      const lh = main.titled ? (labels.get(main.typeId)?.h ?? DEFAULT_LABEL_HEIGHT) + gap : 0;
      units.push({
        vs: g,
        main,
        box,
        w: Math.max(box[2] - box[0], main.titleWidth ?? 0) + 2 * pad,
        h: box[3] - box[1] + lh + 2 * pad,
      });
    }
  }
  return units;
}

class Packer {
  readonly W: number;
  readonly H: number;
  readonly CW: number;
  readonly CH: number;

  constructor(
    readonly NC: number,
    readonly NR: number,
    border: Border
  ) {
    this.W = border.x1 - border.x0;
    this.H = border.y1 - border.y0;
    this.CW = this.W / NC;
    this.CH = this.H / NR;
  }

  /** Smallest track sizes so every item fits its span (greedy by span end). */
  minSizes(items: Item[], n: number, key: (i: Item) => number, span: (i: Item) => [number, number]) {
    const size = new Array<number>(n).fill(0);
    for (let e = 0; e < n; e++) {
      let need = 0;
      for (const it of items) {
        const [s, k] = span(it);
        if (s + k - 1 === e) need = Math.max(need, key(it) - sum(size, s, e));
      }
      size[e] = need;
    }
    return size;
  }

  /** Keep the nominal cell where it fits; wide items push; leftover at the end. */
  tracks(sizes: number[], total: number, nominal: number) {
    const tgt = sizes.map((s) => Math.max(s, nominal));
    if (sum(tgt) <= total) return tgt;
    const extra = total - sum(sizes);
    const deficit = sizes.map((s, i) => tgt[i] - s);
    const d = sum(deficit);
    return d > 0 ? sizes.map((s, i) => s + (extra * deficit[i]) / d) : sizes;
  }

  /** Rows joined by multi-row items share column tracks; other rows are independent. */
  rowGroups(items: Item[]) {
    let g = Array.from({ length: this.NR }, (_, i) => i);
    for (const it of items)
      for (let k = it.r + 1; k < it.r + it.ch; k++) {
        const a = g[it.r];
        const b = g[k];
        g = g.map((x) => (x === b ? a : x));
      }
    return g;
  }

  colTracks(items: Item[], final = false) {
    const g = this.rowGroups(items);
    const out = new Map<number, number[]>();
    for (const key of new Set(g)) {
      const its = items.filter((i) => g[i.r] === key);
      const mins = this.minSizes(its, this.NC, (i) => i.u.w, (i) => [i.c, i.cw]);
      out.set(key, final ? this.tracks(mins, this.W, this.CW) : mins);
    }
    return g.map((key) => out.get(key)!);
  }

  rowMins(items: Item[]) {
    return this.minSizes(items, this.NR, (i) => i.u.h, (i) => [i.r, i.ch]);
  }

  feasible(items: Item[]) {
    return this.colTracks(items).every((cw) => sum(cw) <= this.W + 1e-6) && sum(this.rowMins(items)) <= this.H + 1e-6;
  }

  waste(items: Item[]) {
    const cws = this.colTracks(items);
    const rh = this.rowMins(items);
    return items.reduce(
      (acc, i) => acc + sum(cws[i.r], i.c, i.c + i.cw) * sum(rh, i.r, i.r + i.ch) - i.u.w * i.u.h,
      0
    );
  }

  tryAdd(sheet: PackSheet, u: Unit) {
    const { occ } = sheet;
    for (let cell = 0; cell < this.NR * this.NC; cell++) {
      const r = Math.floor(cell / this.NC);
      const c = cell % this.NC;
      if (occ[r][c]) continue;
      for (let ch = 1; ch <= this.NR - r; ch++)
        for (let cw = 1; cw <= this.NC - c; cw++) {
          let free = true;
          for (let i = 0; i < ch && free; i++) for (let j = 0; j < cw && free; j++) free = !occ[r + i][c + j];
          if (!free) continue;
          const items = [...sheet.items, { r, c, ch, cw, u }];
          if (this.feasible(items)) {
            for (let i = 0; i < ch; i++) for (let j = 0; j < cw; j++) occ[r + i][c + j] = true;
            sheet.items = items;
            return true;
          }
        }
    }
    return false;
  }

  pack1(units: Unit[]): PackSheet[] {
    const sheets: PackSheet[] = [];
    for (const u of units) {
      if (sheets.some((s) => this.tryAdd(s, u))) continue;
      const s: PackSheet = { occ: Array.from({ length: this.NR }, () => new Array<boolean>(this.NC).fill(false)), items: [] };
      sheets.push(s);
      if (!this.tryAdd(s, u)) throw new Error(`unit of view ${u.main.viewId} does not fit`);
    }
    return sheets;
  }

  /** Several orderings (largest first + seeded shuffles); best by (sheets, single small sheets, waste). */
  pack(units: Unit[], tries: number): PackSheet[] {
    const cands: Unit[][] = [
      [...units].sort((a, b) => b.w * b.h - a.w * a.h),
      [...units].sort((a, b) => b.h - a.h),
      [...units].sort((a, b) => b.w - a.w),
    ];
    const random = rng(7);
    const xs = [...units];
    for (let t = 0; t < tries; t++) {
      for (let i = xs.length - 1; i > 0; i--) {
        const j = Math.floor(random() * (i + 1));
        [xs[i], xs[j]] = [xs[j], xs[i]];
      }
      cands.push([...xs]);
    }
    let best: { score: number[]; sheets: PackSheet[] } | null = null;
    for (const c of cands) {
      const s = this.pack1(c);
      const smallSingle = s.filter((x) => x.items.length === 1 && x.items[0].u.w * x.items[0].u.h < 0.4 * this.W * this.H).length;
      const score = [s.length, smallSingle, Math.round(s.reduce((a, x) => a + this.waste(x.items), 0) / 1000)];
      if (!best || compareScores(score, best.score) < 0) best = { score, sheets: s };
    }
    return best!.sheets;
  }
}

function compareScores(a: number[], b: number[]) {
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return a[i] - b[i];
  return 0;
}

/** Splits a sheet number into series prefix + numeric suffix (last two digits of a 3+ digit run, as S8-0 + 01). */
export function splitSheetNumber(num: string): { prefix: string; digits: number; value: number } {
  const m = /(\d+)$/.exec(num);
  if (!m) return { prefix: num + "-", digits: 1, value: 0 };
  const run = m[1];
  const digits = run.length >= 3 ? 2 : run.length;
  return { prefix: num.slice(0, num.length - digits), digits, value: parseInt(run.slice(run.length - digits), 10) };
}

interface ResolvedSeries {
  prefix: string;
  start: number;
  digits: number;
  name: string;
  sheets: MeasuredSheet[];
}

/** Explicit series first (by sheetNumbers or prefix); the rest grouped by number prefix + sheet name. */
export function resolveSeries(sheets: MeasuredSheet[], series: SeriesInput[] | undefined): ResolvedSeries[] {
  const sorted = [...sheets].sort((a, b) => (a.number < b.number ? -1 : a.number > b.number ? 1 : 0));
  const claimed = new Set<MeasuredSheet>();
  const out: ResolvedSeries[] = [];
  for (const se of series ?? []) {
    const members = sorted.filter(
      (s) => !claimed.has(s) && (se.sheetNumbers ? se.sheetNumbers.includes(s.number) : s.number.startsWith(se.prefix))
    );
    if (!members.length) continue;
    members.forEach((s) => claimed.add(s));
    const suffix = members[0].number.startsWith(se.prefix) ? members[0].number.slice(se.prefix.length) : "";
    const parsed = /^\d+$/.test(suffix) ? parseInt(suffix, 10) : 1;
    out.push({
      prefix: se.prefix,
      start: se.start ?? parsed,
      digits: se.digits ?? (/^\d+$/.test(suffix) ? suffix.length : 2),
      name: se.name ?? members[0].name,
      sheets: members,
    });
  }
  const auto = new Map<string, ResolvedSeries>();
  for (const s of sorted) {
    if (claimed.has(s)) continue;
    const { prefix, digits, value } = splitSheetNumber(s.number);
    const key = prefix + "\u0001" + digits + "\u0001" + s.name;
    const existing = auto.get(key);
    if (existing) existing.sheets.push(s);
    else {
      const r: ResolvedSeries = { prefix, digits, start: value, name: s.name, sheets: [s] };
      auto.set(key, r);
      out.push(r);
    }
  }
  return out;
}

/**
 * Plans the whole selection. `otherSheetNumbers` are sheets outside the
 * selection; their numbers are never reused.
 */
export function planGrid(
  sheets: MeasuredSheet[],
  border: Border,
  options: GridOptions,
  series?: SeriesInput[],
  otherSheetNumbers: string[] = []
): GridPlan {
  const { cols: NC, rows: NR, pad: PAD, gap: GAP } = options;
  const packer = new Packer(NC, NR, border);
  const labels = labelConstants(sheets);
  const units = buildUnits(sheets, labels, PAD, GAP);
  const errors: string[] = [];

  // A unit must fit one whole sheet (all columns x all rows)
  const fits = (u: Unit) => u.w <= packer.W + 1e-6 && u.h <= packer.H + 1e-6;
  for (const u of units.filter((u) => !fits(u)))
    errors.push(
      `view ${u.main.viewId}${u.main.viewName ? ` '${u.main.viewName}'` : ""} on ${u.main.sheet.number} needs ` +
        `${round2(u.w)} x ${round2(u.h)} mm, more than the ${round2(packer.W)} x ${round2(packer.H)} mm border`
    );
  if (errors.length) return { sheets: [], emptiedSheets: [], oldToNew: {}, errors };

  const resolved = resolveSeries(sheets, series);
  const owner = new Map<string, ResolvedSeries>();
  for (const se of resolved) for (const s of se.sheets) owner.set(s.number, se);
  const bySheetNumber = new Map(sheets.map((s) => [s.number, s]));
  const taken = new Set(otherSheetNumbers);

  const planned: PlannedSheet[] = [];
  const oldToNew: Record<string, string> = {};
  for (const se of resolved) {
    const members = new Set(se.sheets);
    const us = units.filter((u) => members.has(u.main.sheet));
    if (!us.length) continue;
    let n = 0;
    for (const s of packer.pack(us, options.tries)) {
      // next free number: not outside the selection, not used, not owned by another series
      let num: string;
      for (;;) {
        num = se.prefix + String(se.start + n).padStart(se.digits, "0");
        n++;
        const o = owner.get(num);
        if (!taken.has(num) && (!o || o === se)) break;
      }
      taken.add(num);

      const items = s.items;
      const cws = packer.colTracks(items, true);
      const rh = packer.tracks(packer.rowMins(items), packer.H, packer.CH);
      const ys = Array.from({ length: NR + 1 }, (_, r) => border.y1 - sum(rh, 0, r));
      const viewports: PlannedViewport[] = [];
      const details: string[] = [];
      for (const { r, c, ch, cw, u } of items) {
        const bx0 = border.x0 + sum(cws[r], 0, c);
        const bx1 = bx0 + sum(cws[r], c, c + cw);
        const by0 = ys[r + ch];
        const cell = (r + ch - 1) * NC + c + 1;
        const [x0, y0, x1, y1] = u.box;
        const m = u.main;
        const L = labels.get(m.typeId);
        const lw = Math.min(Math.max(packer.CW - 2 * PAD, x1 - x0, m.titleWidth ?? 0), bx1 - bx0 - 2 * PAD);
        const dx = bx0 + PAD + lw / 2 - (x1 + x0) / 2;
        const dy = by0 + PAD + (m.titled ? (L?.h ?? DEFAULT_LABEL_HEIGHT) + GAP : 0) - y0;
        const ordered = [...u.vs].sort(
          (a, b) => Number(a !== m) - Number(b !== m) || b.box[3] - a.box[3] || a.box[0] - b.box[0]
        );
        let k = 0;
        for (const v of ordered) {
          const det = v === m ? String(cell) : `${cell}${String.fromCharCode(65 + k++)}`;
          const e: PlannedViewport = {
            viewportId: v.viewportId,
            viewId: v.viewId,
            typeId: v.typeId,
            center: { x: round2((v.box[0] + v.box[2]) / 2 + dx), y: round2((v.box[1] + v.box[3]) / 2 + dy) },
            detailNumber: det,
            old: { sheetNumber: v.sheet.number, detailNumber: v.detailNumber },
          };
          if (v === m && m.titled && L && L.kx != null && L.ky != null) {
            e.labelOffset = { x: round2(bx0 + PAD - (v.box[0] + dx) - L.kx), y: round2(by0 + PAD - (v.box[1] + dy) - L.ky) };
            e.labelLineLength = round2(Math.max(0, lw - L.extra));
          }
          if (v === m) details.push(det);
          const before = `${v.detailNumber}/${v.sheet.number}`;
          const after = `${det}/${num}`;
          if (before !== after) oldToNew[before] = after;
          viewports.push(e);
        }
      }
      const existing = bySheetNumber.get(num);
      planned.push({
        number: num,
        name: se.name,
        ...(existing ? { sheetId: existing.id } : {}),
        templateSheetId: se.sheets[0].id,
        occupancy: s.occ.map((row) => row.map((x) => (x ? "#" : ".")).join("")),
        details: details.sort((a, b) => parseInt(a, 10) - parseInt(b, 10)),
        viewports,
      });
    }
  }

  const kept = new Set(planned.map((p) => p.number));
  return {
    sheets: planned,
    emptiedSheets: sheets.filter((s) => !kept.has(s.number)).map((s) => ({ id: s.id, number: s.number })),
    oldToNew,
    errors,
  };
}

/** One line per planned sheet: number, occupancy map, detail numbers. */
export function describePlan(plan: GridPlan): string[] {
  return plan.sheets.map((s) => {
    const subs = s.viewports.length - s.details.length;
    return (
      `${s.number}${s.sheetId ? "" : " (new)"}  ${s.occupancy.join(" ")}  ${s.details.join(" ")}` +
      (subs > 0 ? `  +${subs} sub-view${subs > 1 ? "s" : ""}` : "")
    );
  });
}
