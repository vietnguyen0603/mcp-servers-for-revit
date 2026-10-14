/**
 * Pure geometry for the array tools: parallel copies of a truss line
 * (create_trusses `array`) and purlin/girt layouts between two bounding lines
 * (create_member_array). All values in millimetres; z is measured from the
 * item's level. No Revit access here, so it is unit-tested directly.
 */

export interface Xy {
  x: number;
  y: number;
}

export interface Xyz {
  x: number;
  y: number;
  z?: number;
}

export interface LineXyz {
  start: Xyz;
  end: Xyz;
}

const EPS = 1e-9;

const round = (value: number) => Math.round(value * 1000) / 1000;

/** Marks for N copies: "T1" -> T1, T2, ...; "PL" -> PL-1, PL-2, ... ; a number counts up. */
export function arrayMarks(mark: string | number | undefined, count: number): Array<string | number | undefined> {
  if (mark === undefined || count <= 1) return Array.from({ length: count }, () => mark);
  if (typeof mark === "number") return Array.from({ length: count }, (_, i) => mark + i);
  const match = /^(.*?)(\d+)$/.exec(mark);
  if (match) {
    const [, prefix, digits] = match;
    const first = Number(digits);
    return Array.from({ length: count }, (_, i) => `${prefix}${String(first + i).padStart(digits.length, "0")}`);
  }
  return Array.from({ length: count }, (_, i) => `${mark}-${i + 1}`);
}

// ---------------------------------------------------------------- trusses

export interface PlanArray {
  count?: number;
  spacing?: number;
  distance?: number;
  direction?: Xy | "left" | "right";
}

/** Count and spacing of a plan array from any two of count / spacing / distance. */
export function resolvePlanArray(array: PlanArray): { count: number; spacing: number } {
  const { count, spacing, distance } = array;
  const given = [count, spacing, distance].filter((v) => v !== undefined).length;
  if (given === 3) throw new Error("array: give two of count, spacing and distance, not all three");
  if (count !== undefined && spacing !== undefined) return { count, spacing };
  if (distance !== undefined && spacing !== undefined) {
    return { count: Math.floor(distance / spacing + 1e-6) + 1, spacing };
  }
  if (distance !== undefined && count !== undefined) {
    if (count < 2) throw new Error("array: count must be at least 2 with distance");
    return { count, spacing: distance / (count - 1) };
  }
  if (count === 1) return { count: 1, spacing: 0 };
  throw new Error("array: give count + spacing, distance + spacing, or distance + count");
}

/** Unit plan vector of the array; "left" (default) / "right" are perpendicular to start -> end. */
export function arrayDirection(start: Xy, end: Xy, direction: PlanArray["direction"]): Xy {
  if (typeof direction === "object") {
    const length = Math.hypot(direction.x, direction.y);
    if (length < EPS) throw new Error("array.direction must not be zero");
    return { x: direction.x / length, y: direction.y / length };
  }
  const dx = end.x - start.x;
  const dy = end.y - start.y;
  const length = Math.hypot(dx, dy);
  if (length < EPS) throw new Error("start and end are the same point");
  const left = { x: -dy / length, y: dx / length };
  return direction === "right" ? { x: -left.x, y: -left.y } : left;
}

/**
 * Expands one item with `array` into parallel copies offset along the array
 * direction; the copies drop `array` and get counted marks.
 */
export function expandPlanArray<T extends { start: Xy; end: Xy; array?: PlanArray; mark?: string | number }>(
  item: T
): Array<Omit<T, "array">> {
  const { array, ...rest } = item;
  if (!array) return [rest];
  const { count, spacing } = resolvePlanArray(array);
  const unit = arrayDirection(item.start, item.end, array.direction);
  const marks = arrayMarks(item.mark, count);
  return Array.from({ length: count }, (_, i) => {
    const dx = unit.x * spacing * i;
    const dy = unit.y * spacing * i;
    const copy = {
      ...rest,
      start: { x: round(item.start.x + dx), y: round(item.start.y + dy) },
      end: { x: round(item.end.x + dx), y: round(item.end.y + dy) },
    } as Omit<T, "array">;
    if (marks[i] !== undefined) (copy as { mark?: string | number }).mark = marks[i];
    return copy;
  });
}

// ---------------------------------------------------------------- member arrays

interface V3 {
  x: number;
  y: number;
  z: number;
}

const v3 = (p: Xyz): V3 => ({ x: p.x, y: p.y, z: p.z ?? 0 });
const sub = (a: V3, b: V3): V3 => ({ x: a.x - b.x, y: a.y - b.y, z: a.z - b.z });
const add = (a: V3, b: V3): V3 => ({ x: a.x + b.x, y: a.y + b.y, z: a.z + b.z });
const scale = (a: V3, s: number): V3 => ({ x: a.x * s, y: a.y * s, z: a.z * s });
const dot = (a: V3, b: V3) => a.x * b.x + a.y * b.y + a.z * b.z;
const norm = (a: V3) => Math.sqrt(dot(a, a));
const lerp = (a: V3, b: V3, t: number): V3 => add(a, scale(sub(b, a), t));

export interface MemberLayout {
  baseLine: LineXyz;
  farLine?: LineXyz;
  direction?: Xyz;
  distance?: number;
  spacing?: number;
  count?: number;
  maxSpacing?: number;
  firstOffset?: number;
  lastOffset?: number;
  includeEnd?: boolean;
}

/** Distances (mm, along the layout direction) of the members from the base line. */
export function layoutPositions(total: number, layout: MemberLayout): number[] {
  const first = layout.firstOffset ?? 0;
  const last = layout.lastOffset ?? 0;
  const span = total - first - last;
  if (span < -1e-6) throw new Error(`firstOffset + lastOffset (${first + last} mm) exceed the layout depth ${round(total)} mm`);
  const modes = [layout.spacing, layout.count, layout.maxSpacing].filter((v) => v !== undefined).length;
  if (modes !== 1) throw new Error("give exactly one of spacing, count or maxSpacing");

  if (layout.spacing !== undefined) {
    const positions: number[] = [];
    for (let s = first; s <= first + span + 1e-6; s += layout.spacing) positions.push(s);
    const end = first + span;
    if (layout.includeEnd && end - positions[positions.length - 1] > 1) positions.push(end);
    return positions;
  }
  let count: number;
  if (layout.count !== undefined) {
    count = layout.count;
  } else {
    count = Math.max(2, Math.ceil(span / layout.maxSpacing! - 1e-9) + 1);
    if (span < 1e-6) count = 1;
  }
  if (count === 1) return [first];
  return Array.from({ length: count }, (_, i) => first + (span * i) / (count - 1));
}

/** Plan point-in-polygon (even-odd). */
export function insidePolygon(p: Xy, polygon: Xy[]): boolean {
  let inside = false;
  for (let i = 0, j = polygon.length - 1; i < polygon.length; j = i++) {
    const a = polygon[i];
    const b = polygon[j];
    if (a.y > p.y !== b.y > p.y && p.x < ((b.x - a.x) * (p.y - a.y)) / (b.y - a.y) + a.x) inside = !inside;
  }
  return inside;
}

function onBoundary(p: Xy, polygon: Xy[], tolerance: number): boolean {
  for (let i = 0; i < polygon.length; i++) {
    const a = polygon[i];
    const b = polygon[(i + 1) % polygon.length];
    const dx = b.x - a.x;
    const dy = b.y - a.y;
    const lengthSquared = dx * dx + dy * dy;
    const t = lengthSquared < EPS ? 0 : Math.max(0, Math.min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / lengthSquared));
    if (Math.hypot(a.x + dx * t - p.x, a.y + dy * t - p.y) <= tolerance) return true;
  }
  return false;
}

/** Parameter intervals [t0, t1] of segment a -> b (plan) inside the polygon; pieces on its edges (0.5 mm) count as inside. */
export function clipToPolygon(a: Xy, b: Xy, polygon: Xy[]): Array<[number, number]> {
  const ts = [0, 1];
  const d = { x: b.x - a.x, y: b.y - a.y };
  for (let i = 0; i < polygon.length; i++) {
    const p = polygon[i];
    const q = polygon[(i + 1) % polygon.length];
    const e = { x: q.x - p.x, y: q.y - p.y };
    const denom = d.x * e.y - d.y * e.x;
    if (Math.abs(denom) < EPS) continue;
    const w = { x: p.x - a.x, y: p.y - a.y };
    const t = (w.x * e.y - w.y * e.x) / denom;
    const u = (w.x * d.y - w.y * d.x) / denom;
    if (t > 0 && t < 1 && u >= -1e-9 && u <= 1 + 1e-9) ts.push(t);
  }
  ts.sort((x, y) => x - y);
  const intervals: Array<[number, number]> = [];
  for (let i = 0; i < ts.length - 1; i++) {
    const t0 = ts[i];
    const t1 = ts[i + 1];
    if (t1 - t0 < 1e-9) continue;
    const mid = (t0 + t1) / 2;
    const point = { x: a.x + d.x * mid, y: a.y + d.y * mid };
    if (!insidePolygon(point, polygon) && !onBoundary(point, polygon, 0.5)) continue;
    const previous = intervals[intervals.length - 1];
    if (previous && Math.abs(previous[1] - t0) < 1e-9) previous[1] = t1;
    else intervals.push([t0, t1]);
  }
  return intervals;
}

export interface GeneratedMember {
  start: Xy;
  end: Xy;
  startOffset: number;
  endOffset: number;
  /** Cross-section rotation that lays the section in the slope plane (degrees). */
  slopeRotationDeg: number;
  /** Distance from the base line along the layout (mm). */
  position: number;
}

/**
 * Members parallel to the base line: interpolated between baseLine and
 * farLine (spacing measured perpendicular to the base line in 3D, i.e. along
 * a roof slope), or offset along `direction` up to `distance`. Optional plan
 * clipping to `boundary`; pieces shorter than `minLength` are dropped.
 */
export function generateMembers(layout: MemberLayout, boundary?: Xy[], minLength = 10): GeneratedMember[] {
  const bs = v3(layout.baseLine.start);
  const be = v3(layout.baseLine.end);
  const axis = sub(be, bs);
  const axisLength = norm(axis);
  if (axisLength < 1) throw new Error("baseLine start and end are the same point");
  if (Math.hypot(axis.x, axis.y) < 1) throw new Error("baseLine must not be vertical");
  const a = scale(axis, 1 / axisLength);

  let total: number;
  let at: (s: number) => [V3, V3];
  let across: V3;
  if (layout.farLine) {
    if (layout.direction || layout.distance !== undefined) throw new Error("give farLine or direction + distance, not both");
    const fs = v3(layout.farLine.start);
    const fe = v3(layout.farLine.end);
    const v = sub(scale(add(fs, fe), 0.5), scale(add(bs, be), 0.5));
    across = sub(v, scale(a, dot(v, a)));
    total = norm(across);
    if (total < 1) throw new Error("farLine lies on the baseLine");
    at = (s) => [lerp(bs, fs, s / total), lerp(be, fe, s / total)];
  } else {
    if (!layout.direction || layout.distance === undefined) throw new Error("give farLine, or direction + distance");
    const d = v3(layout.direction);
    const length = norm(d);
    if (length < EPS) throw new Error("direction must not be zero");
    const u = scale(d, 1 / length);
    if (norm(sub(u, scale(a, dot(u, a)))) < 1e-6) throw new Error("direction is parallel to the baseLine");
    across = u;
    total = layout.distance;
    at = (s) => [add(bs, scale(u, s)), add(be, scale(u, s))];
  }

  // Slope of the plane spanned by the member axis and the layout direction.
  const acrossPerp = sub(across, scale(a, dot(across, a)));
  const horizontal = Math.hypot(acrossPerp.x, acrossPerp.y);
  const slope = (Math.atan2(Math.abs(acrossPerp.z), horizontal) * 180) / Math.PI;
  // Rising side: the plan direction in which the plane goes up.
  const rise = acrossPerp.z >= 0 ? acrossPerp : scale(acrossPerp, -1);
  const left = { x: -a.y, y: a.x };
  const side = rise.x * left.x + rise.y * left.y >= 0 ? 1 : -1;
  const slopeRotationDeg = Math.abs(acrossPerp.z) < 1e-9 ? 0 : round(side * slope);

  const members: GeneratedMember[] = [];
  for (const position of layoutPositions(total, layout)) {
    const [p0, p1] = at(position);
    const pieces: Array<[number, number]> = boundary ? clipToPolygon(p0, p1, boundary) : [[0, 1]];
    for (const [t0, t1] of pieces) {
      const q0 = lerp(p0, p1, t0);
      const q1 = lerp(p0, p1, t1);
      if (norm(sub(q1, q0)) < minLength) continue;
      members.push({
        start: { x: round(q0.x), y: round(q0.y) },
        end: { x: round(q1.x), y: round(q1.y) },
        startOffset: round(q0.z),
        endOffset: round(q1.z),
        slopeRotationDeg,
        position: round(position),
      });
    }
  }
  return members;
}
