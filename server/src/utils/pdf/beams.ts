/** Beams from labels + parallel edge lines (port of beams.py). */
import { type Pt, add, canonicalDir, dot, mergeIntervals, perp, round, scale, sub, unit, len } from "./geometry.js";
import type { Ctx, ModelText } from "./context.js";
import { fitLine } from "./calibration.js";

export const DEFAULT_BEAM_LABEL =
  /^(?:(L[\w\-,]*|B\d|ROOF|RF|TM)\.)?((?:[HV]B|GB|TB|B|CT|HB|DB|WB)[\d.]*[A-Z]?)(?:-(\d+)x(\d+))?$/;
const TYPE_RE = /^B-(\d+)x(\d+)$/;

export interface BeamOptions {
  labelPattern?: RegExp;
  /** Extra name -> [W, H] sizes (merged over the on-sheet schedule) */
  sizes?: Record<string, [number, number]>;
  /** Search half-length along the beam from the label (mm) */
  reach?: number;
  /** Max distance from the label to a run of the edge (mm) */
  labelGap?: number;
  /** Gap bridged when merging dashed edge pieces (mm) */
  dashGap?: number;
  minLength?: number;
}

export interface BeamOut {
  name: string;
  W: number;
  H: number;
  start: { x: number; y: number };
  end: { x: number; y: number };
  labels?: number;
}

/** name -> (W,H) from schedule rows: a name text followed on the same row by "B-WxH". */
export function readSchedule(texts: ModelText[]): { sizes: Map<string, [number, number]>; rows: Set<ModelText> } {
  const sizes = new Map<string, [number, number]>();
  const rows = new Set<ModelText>();
  const types = texts.filter((t) => TYPE_RE.test(t.text) && Math.abs(t.page.dir[1]) < 1e-3);
  for (const t of texts) {
    if (TYPE_RE.test(t.text) || Math.abs(t.page.dir[1]) > 1e-3) continue;
    const right = t.page.ox + t.page.width;
    const cands = types.filter((tt) => Math.abs(tt.page.y - t.page.y) < Math.max(2, 0.3 * t.page.size) && tt.page.ox - right > 0 && tt.page.ox - right < 120);
    if (!cands.length) continue;
    cands.sort((p, q) => p.page.ox - q.page.ox);
    const m = TYPE_RE.exec(cands[0].text)!;
    sizes.set(t.text, [+m[1], +m[2]]);
    rows.add(t);
  }
  return { sizes, rows };
}

interface Group {
  o: number;
  iv: [number, number][];
  segs: { a: Pt; b: Pt; s0: number; s1: number }[];
}

function linesNear(segs: { a: Pt; b: Pt }[], c: Pt, u: Pt, W: number, reach: number, dashGap: number): Group[] {
  const n = perp(u);
  const cl: { o: number; s0: number; s1: number; a: Pt; b: Pt }[] = [];
  for (const { a, b } of segs) {
    const v = sub(b, a);
    const L = len(v);
    if (L < 30) continue;
    // parallel to the label direction within ~0.8 deg (label angles are less precise than the lines)
    const cross = Math.abs(v[0] * u[1] - v[1] * u[0]);
    if (cross > L * 0.0141) continue;
    const o = dot(sub(a, c), n);
    if (Math.abs(o) > W + 1500) continue;
    const s0 = Math.min(dot(sub(a, c), u), dot(sub(b, c), u));
    const s1 = Math.max(dot(sub(a, c), u), dot(sub(b, c), u));
    if (s1 < -reach || s0 > reach) continue;
    cl.push({ o: (o + dot(sub(b, c), n)) / 2, s0, s1, a, b });
  }
  cl.sort((p, q) => p.o - q.o);
  const groups: Group[] = [];
  for (const e of cl) {
    const g = groups[groups.length - 1];
    if (g && Math.abs(g.o - e.o) < 8) {
      g.iv.push([e.s0, e.s1]);
      g.segs.push(e);
    } else groups.push({ o: e.o, iv: [[e.s0, e.s1]], segs: [e] });
  }
  for (const g of groups) g.iv = mergeIntervals(g.iv, dashGap);
  return groups;
}

function compAt(iv: [number, number][], s: number, tol: number): [number, number] | null {
  let best: [number, number, number] | null = null;
  for (const [a, b] of iv) {
    const d = a <= s && s <= b ? 0 : Math.min(Math.abs(a - s), Math.abs(b - s));
    if (d <= tol && (!best || d < best[0])) best = [d, a, b];
  }
  return best ? [best[1], best[2]] : null;
}

function dedupe(bs: BeamOut[]): BeamOut[] {
  const res: BeamOut[] = [];
  for (const b of bs) {
    const A: Pt = [b.start.x, b.start.y];
    const B: Pt = [b.end.x, b.end.y];
    const u = unit(sub(B, A));
    let merged = false;
    for (const r of res) {
      if (r.W !== b.W || r.H !== b.H) continue;
      const RA: Pt = [r.start.x, r.start.y];
      const RB: Pt = [r.end.x, r.end.y];
      const ru = unit(sub(RB, RA));
      if (Math.abs(Math.abs(dot(u, ru)) - 1) > 1e-3) continue;
      if (Math.abs(dot(sub(A, RA), perp(ru))) > 20) continue;
      const a0 = Math.min(dot(sub(A, RA), ru), dot(sub(B, RA), ru));
      const a1 = Math.max(dot(sub(A, RA), ru), dot(sub(B, RA), ru));
      const r1 = dot(sub(RB, RA), ru);
      if (a0 > r1 + 50 || a1 < -50) continue;
      const lo = Math.min(a0, 0);
      const hi = Math.max(a1, r1);
      const P0 = add(RA, scale(ru, lo));
      const P1 = add(RA, scale(ru, hi));
      r.start = { x: round(P0[0]), y: round(P0[1]) };
      r.end = { x: round(P1[0]), y: round(P1[1]) };
      r.labels = (r.labels ?? 1) + 1;
      merged = true;
      break;
    }
    if (!merged) res.push({ ...b });
  }
  return res;
}

export function extractBeams(ctx: Ctx, opt: BeamOptions = {}) {
  const texts = ctx.texts();
  const { sizes: sch, rows } = readSchedule(texts);
  for (const [k, v] of Object.entries(opt.sizes ?? {})) sch.set(k, v);
  const re = opt.labelPattern ?? DEFAULT_BEAM_LABEL;
  const reach = opt.reach ?? 20000;
  const segs = ctx.modelSegs({ inBoxMargin: reach });
  const out: BeamOut[] = [];
  const missing: { name: string; W: number; H: number; x: number; y: number }[] = [];
  const unsized: string[] = [];
  for (const t of texts) {
    if (rows.has(t)) continue;
    const m = re.exec(t.text);
    if (!m || !ctx.inBox(t.p)) continue;
    const name = t.text.replace(/-\d+x\d+$/, "");
    let W: number;
    let H: number;
    const sized = /-(\d+)x(\d+)$/.exec(t.text);
    if (sized) [W, H] = [+sized[1], +sized[2]];
    else if (sch.has(name)) [W, H] = sch.get(name)!;
    else if (sch.has(t.text)) [W, H] = sch.get(t.text)!;
    else {
      unsized.push(t.text);
      continue;
    }
    const u = canonicalDir(t.dir);
    const c = t.p;
    const gs = linesNear(segs, c, u, W, reach, opt.dashGap ?? 900);
    let best: [number, number, number, number, Group, Group] | null = null;
    for (let i = 0; i < gs.length; i++) {
      for (let j = i + 1; j < gs.length; j++) {
        const g1 = gs[i];
        const g2 = gs[j];
        if (Math.abs(Math.abs(g2.o - g1.o) - W) > Math.max(30, 0.03 * W)) continue;
        const c1 = compAt(g1.iv, 0, opt.labelGap ?? 1500);
        const c2 = compAt(g2.iv, 0, opt.labelGap ?? 1500);
        if (!c1 || !c2) continue;
        const lo = Math.max(c1[0], c2[0]);
        const hi = Math.min(c1[1], c2[1]);
        if (hi - lo < (opt.minLength ?? 300)) continue;
        const sc = Math.max(Math.abs(g1.o), Math.abs(g2.o));
        if (!best || sc < best[0]) best = [sc, (g1.o + g2.o) / 2, lo, hi, g1, g2];
      }
    }
    if (!best) {
      missing.push({ name, W, H, x: round(c[0]), y: round(c[1]) });
      continue;
    }
    const [, o, lo, hi, g1, g2] = best;
    const n = perp(u);
    let p0 = add(add(c, scale(n, o)), scale(u, lo));
    let p1 = add(add(c, scale(n, o)), scale(u, hi));
    // refine the axis from the edge pieces themselves (label angles are approximate)
    const pick = (g: Group) => g.segs.filter((e) => e.s1 >= lo - 1 && e.s0 <= hi + 1);
    const f1 = fitLine(pick(g1));
    const f2 = fitLine(pick(g2));
    if (f1 && f2) {
      let fu = f1.u;
      if (dot(fu, f2.u) < 0) fu = scale(fu, -1);
      fu = unit(add(fu, dot(f2.u, fu) < 0 ? scale(f2.u, -1) : f2.u));
      if (dot(fu, u) < 0) fu = scale(fu, -1);
      const pc = scale(add(f1.p, add(f2.p, scale(fu, dot(sub(f1.p, f2.p), fu)))), 0.5);
      p0 = add(pc, scale(fu, dot(sub(p0, pc), fu)));
      p1 = add(pc, scale(fu, dot(sub(p1, pc), fu)));
    }
    out.push({ name, W, H, start: { x: round(p0[0]), y: round(p0[1]) }, end: { x: round(p1[0]), y: round(p1[1]) } });
  }
  return {
    beams: dedupe(out),
    missing,
    unsizedLabels: [...new Set(unsized)].slice(0, 50),
    schedule: Object.fromEntries([...sch.entries()].map(([k, v]) => [k, `${v[0]}x${v[1]}`])),
  };
}
