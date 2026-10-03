import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { elementIdSchema, parameterValuesSchema } from "../utils/documentationSchemas.js";
import { dimensionSegmentTextSchema, dimensionTextSchema } from "../utils/dimensionSchemas.js";
import { leaderAttachmentSchema, textNoteFormatSchema } from "./create_text_note.js";
import {
  addTagIds,
  createDetailConverter,
  type DetailConverter,
  type DetailUnits,
  type Point2,
  resolveTagReference,
  type TagMap,
} from "../utils/detailUnits.js";
import {
  type ItemOutcome,
  readIds,
  readWarnings,
  resolveViewInfo,
  type RevitSender,
  runChunkedBatch,
} from "../utils/revitBatch.js";

const CHUNK_SIZE = 150;

const pt = z.object({ x: z.number().finite(), y: z.number().finite() }).strict().describe("Point in the chosen units");
const tag = z.string().min(1).max(128).optional().describe("Name to reference this element's ids from dimensions");
const lineStyle = z.string().min(1).max(256);
const typeName = z.string().min(1).max(256);
const dimensionEnds = ["curve", "start", "end", "center", "left", "right", "front", "back", "top", "bottom"] as const;

const regionSchema = z
  .object({
    tag,
    points: z.array(pt).min(3).max(1000).describe("Outer boundary"),
    holes: z.array(z.array(pt).min(3).max(1000)).max(100).optional(),
    type: typeName.optional().describe("Filled region type name, e.g. 'Concrete'"),
    typeId: elementIdSchema.optional(),
    masking: z.boolean().optional(),
    lineStyle: lineStyle.optional().describe("Boundary line style ('Invisible' hides it)"),
  })
  .strict();

const lineSchema = z.discriminatedUnion("kind", [
  z.object({ kind: z.literal("line"), tag, start: pt, end: pt, lineStyle: lineStyle.optional() }).strict(),
  z
    .object({
      kind: z.literal("arc"),
      tag,
      center: pt,
      radius: z.number().positive(),
      startAngleDeg: z.number().finite().optional().default(0),
      endAngleDeg: z.number().finite().optional().default(360),
      lineStyle: lineStyle.optional(),
    })
    .strict(),
  z
    .object({
      kind: z.literal("poly"),
      tag,
      points: z.array(pt).min(2).max(1000),
      closed: z.boolean().optional(),
      filletRadius: z.number().finite().nonnegative().optional(),
      lineStyle: lineStyle.optional(),
    })
    .strict(),
]);

const familyFields = {
  family: typeName.optional().describe("Family name (or 'Family : Type')"),
  type: typeName.optional().describe("Type name"),
  typeId: elementIdSchema.optional(),
};

const componentSchema = z
  .object({
    tag,
    ...familyFields,
    at: pt.optional().describe("Insertion point (point-based families)"),
    start: pt.optional().describe("Line-based families"),
    end: pt.optional(),
    rotationDegrees: z.number().finite().optional(),
    parameters: parameterValuesSchema.optional(),
  })
  .strict();

const symbolSchema = z
  .object({
    tag,
    ...familyFields,
    at: pt,
    rotationDegrees: z.number().finite().optional(),
    parameters: parameterValuesSchema.optional(),
    leaders: z.array(z.object({ end: pt, elbow: pt.optional() }).strict()).max(20).optional(),
  })
  .strict();

const textSchema = z
  .object({
    tag,
    text: z.string().min(1).max(4096),
    at: pt.describe("Top-left of the text box for Left alignment"),
    type: typeName.optional().describe("Text type name, e.g. '1/8\" Arial'"),
    typeId: elementIdSchema.optional(),
    width: z.number().positive().max(10000).optional().describe("Wrap width: paper mm (px mode: image px)"),
    align: z.enum(["Left", "Center", "Right"]).optional(),
    rotationDegrees: z.number().finite().optional(),
    leaders: z
      .array(
        z
          .object({
            end: pt,
            elbow: pt.optional(),
            side: z.enum(["Left", "Right"]).optional(),
            shape: z.enum(["Straight", "Arc"]).optional(),
          })
          .strict()
      )
      .max(50)
      .optional(),
    leaderLeftAttachment: leaderAttachmentSchema.optional(),
    leaderRightAttachment: leaderAttachmentSchema.optional(),
    format: textNoteFormatSchema.optional(),
  })
  .strict();

const textPositionFields = {
  position: pt.optional().describe("Dragged text position (chosen units)"),
  leader: z.boolean().optional(),
};

const dimensionSchema = z
  .object({
    tag,
    refs: z
      .array(
        z.union([
          z
            .object({
              tag: z.string().min(1).max(128),
              index: z.number().int().min(-1000).max(1000).optional().describe("Which id of the tag (poly segment); default 0, negative from the end"),
              end: z.enum(dimensionEnds).optional(),
            })
            .strict(),
          z.object({ elementId: elementIdSchema, end: z.enum(dimensionEnds).optional() }).strict(),
        ])
      )
      .min(2)
      .max(200)
      .describe("References in chain order"),
    through: pt.describe("Point the dimension line passes through"),
    direction: z
      .union([z.enum(["h", "v"]), pt])
      .describe("Measured direction: 'h' horizontal, 'v' vertical, or a vector in the chosen units"),
    type: typeName.optional().describe("Dimension type name"),
    typeId: elementIdSchema.optional(),
    text: dimensionTextSchema.extend(textPositionFields).optional(),
    segments: z.array(dimensionSegmentTextSchema.extend(textPositionFields)).min(1).max(500).optional(),
  })
  .strict();

const createViewSchema = z
  .object({
    name: z.string().min(1).max(256),
    scale: z.number().int().min(1).max(100000),
    detailLevel: z.enum(["Coarse", "Medium", "Fine"]).optional(),
    titleOnSheet: z.string().max(256).optional().describe("'Title on Sheet' parameter"),
  })
  .strict();

export const drawDetailShape = {
  viewId: elementIdSchema.optional().describe("Drafting/detail view to draw in (default active view)"),
  createView: createViewSchema.optional().describe("Create a new drafting view and draw in it"),
  units: z.enum(["mm", "in", "ft", "px"]).optional().default("mm"),
  origin: pt.optional().describe("Input point mapped to model (0,0); px mode: image pixel"),
  pxPerPaperInch: z.number().positive().max(100000).optional().describe("px mode: image pixels per paper inch (default 166.667 = 7000 px / 42\")"),
  scale: z.number().int().min(1).max(100000).optional().describe("px mode: view scale (default createView.scale, else the view's)"),
  snapIn: z.number().positive().max(120).optional().describe("Snap converted points to multiples of this many real inches, e.g. 0.25"),
  regions: z.array(regionSchema).max(500).optional(),
  lines: z.array(lineSchema).max(3000).optional(),
  components: z.array(componentSchema).max(1000).optional(),
  symbols: z.array(symbolSchema).max(500).optional(),
  texts: z.array(textSchema).max(500).optional(),
  dimensions: z.array(dimensionSchema).max(500).optional(),
};

type DrawDetailArgs = z.infer<z.ZodObject<typeof drawDetailShape>>;

const KINDS = ["regions", "lines", "components", "symbols", "texts", "dimensions"] as const;
type Kind = (typeof KINDS)[number];

export interface DrawDetailFailure {
  kind: Kind | "view";
  index?: number;
  tag?: string;
  message: string;
}

export interface DrawDetailResult {
  viewId?: number;
  created: Partial<Record<Kind, string>>;
  tags: Record<string, number[]>;
  failures: DrawDetailFailure[];
  warnings: string[];
}

const COMMANDS: Record<Kind, { command: string; listKey: string; idField: string; perItemView: boolean }> = {
  regions: { command: "create_filled_region", listKey: "regions", idField: "filledRegionId", perItemView: false },
  lines: { command: "create_detail_lines", listKey: "lines", idField: "detailCurveIds", perItemView: false },
  components: { command: "place_detail_component", listKey: "components", idField: "instanceId", perItemView: false },
  symbols: { command: "place_annotation_symbol", listKey: "symbols", idField: "elementId", perItemView: false },
  texts: { command: "create_text_note", listKey: "notes", idField: "textNoteId", perItemView: true },
  dimensions: { command: "create_dimensions", listKey: "dimensions", idField: "id", perItemView: true },
};

function familyPayload(item: { family?: string; type?: string; typeId?: number }) {
  return {
    ...(item.typeId !== undefined ? { familyTypeId: item.typeId } : {}),
    ...(item.family ? { familyName: item.family } : {}),
    ...(item.type ? { typeName: item.type } : {}),
  };
}

function definedOnly<T extends Record<string, unknown>>(value: T): T {
  return Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined)) as T;
}

type Built = { payload?: Record<string, unknown>; error?: string; warnings?: string[] };

function buildRegion(r: z.infer<typeof regionSchema>, c: DetailConverter): Built {
  return {
    payload: definedOnly({
      boundary: r.points.map(c.point),
      holes: r.holes?.map((hole) => hole.map(c.point)),
      filledRegionTypeId: r.typeId,
      filledRegionTypeName: r.type,
      masking: r.masking,
      lineStyle: r.lineStyle,
    }),
  };
}

type LineItem = z.infer<typeof lineSchema>;

function buildLine(l: LineItem, c: DetailConverter): Built {
  switch (l.kind) {
    case "line":
      return { payload: definedOnly({ start: c.point(l.start), end: c.point(l.end), lineStyle: l.lineStyle }) };
    case "arc":
      if (l.endAngleDeg <= l.startAngleDeg) return { error: "endAngleDeg must exceed startAngleDeg" };
      return {
        payload: definedOnly({
          center: c.point(l.center),
          radius: c.length(l.radius),
          startAngleDeg: l.startAngleDeg,
          endAngleDeg: l.endAngleDeg,
          lineStyle: l.lineStyle,
        }),
      };
    case "poly":
      return {
        payload: definedOnly({
          points: l.points.map(c.point),
          closed: l.closed,
          filletRadius: l.filletRadius === undefined ? undefined : c.length(l.filletRadius),
          lineStyle: l.lineStyle,
        }),
      };
  }
}

type ComponentItem = z.infer<typeof componentSchema>;

function buildComponent(item: ComponentItem, c: DetailConverter): Built {
  if (item.typeId === undefined && !item.family && !item.type) return { error: "Provide family/type or typeId" };
  if (!item.at && !(item.start && item.end)) return { error: "Provide at (point-based) or start and end (line-based)" };
  return {
    payload: definedOnly({
      ...familyPayload(item),
      location: item.at ? c.point(item.at) : undefined,
      start: item.start && item.end && !item.at ? c.point(item.start) : undefined,
      end: item.start && item.end && !item.at ? c.point(item.end) : undefined,
      rotationDegrees: item.rotationDegrees,
      parameters: item.parameters,
    }),
  };
}

type SymbolItem = z.infer<typeof symbolSchema>;

function buildSymbol(item: SymbolItem, c: DetailConverter): Built {
  if (item.typeId === undefined && !item.family && !item.type) return { error: "Provide family/type or typeId" };
  return {
    payload: definedOnly({
      ...familyPayload(item),
      location: c.point(item.at),
      rotationDegrees: item.rotationDegrees,
      parameters: item.parameters,
      leaders: item.leaders?.map((leader) =>
        definedOnly({ end: c.point(leader.end), elbow: leader.elbow ? c.point(leader.elbow) : undefined })
      ),
    }),
  };
}

type TextItem = z.infer<typeof textSchema>;

function buildText(item: TextItem, c: DetailConverter, viewId: number): Built {
  return {
    payload: definedOnly({
      text: item.text,
      location: c.point(item.at),
      viewId,
      textNoteTypeId: item.typeId,
      textNoteTypeName: item.typeId === undefined ? item.type : undefined,
      width: item.width === undefined ? undefined : c.paperWidth(item.width),
      horizontalAlignment: item.align,
      rotationDegrees: item.rotationDegrees,
      leaders: item.leaders?.map((leader) =>
        definedOnly({
          end: c.point(leader.end),
          elbow: leader.elbow ? c.point(leader.elbow) : undefined,
          side: leader.side,
          shape: leader.shape,
        })
      ),
      leaderLeftAttachment: item.leaderLeftAttachment,
      leaderRightAttachment: item.leaderRightAttachment,
      format: item.format,
    }),
  };
}

type DimensionItem = z.infer<typeof dimensionSchema>;

function convertDimensionText<T extends { position?: Point2 }>(text: T, c: DetailConverter): T {
  return text.position ? { ...text, position: c.point(text.position) } : text;
}

/** Direction in model space (unit vector); px mode flips y like points. */
export function dimensionDirection(direction: DimensionItem["direction"], units: DetailUnits): Point2 | undefined {
  if (direction === "h") return { x: 1, y: 0 };
  if (direction === "v") return { x: 0, y: 1 };
  const x = direction.x;
  const y = units === "px" ? -direction.y : direction.y;
  const length = Math.hypot(x, y);
  return length > 1e-9 ? { x: x / length, y: y / length } : undefined;
}

function buildDimension(item: DimensionItem, c: DetailConverter, viewId: number, tags: TagMap): Built {
  const warnings: string[] = [];
  const references: Array<{ elementId: number; end?: string }> = [];
  for (const ref of item.refs) {
    if ("elementId" in ref) {
      references.push(definedOnly({ elementId: ref.elementId, end: ref.end }));
      continue;
    }
    const resolved = resolveTagReference(tags, ref);
    if (!resolved.ok) return { error: resolved.message };
    if (resolved.warning) warnings.push(resolved.warning);
    references.push(definedOnly({ elementId: resolved.id, end: ref.end }));
  }
  const direction = dimensionDirection(item.direction, c.units);
  if (!direction) return { error: "direction vector must not be zero" };
  const through = c.point(item.through);
  // 100 mm span only sets the dimension direction; the references define what is measured.
  const endPoint = { x: through.x + direction.x * 100, y: through.y + direction.y * 100 };
  return {
    warnings,
    payload: definedOnly({
      viewId,
      startPoint: through,
      endPoint,
      linePoint: through,
      references,
      dimensionStyleId: item.typeId,
      dimensionType: item.typeId === undefined ? item.type : undefined,
      text: item.text ? convertDimensionText(item.text, c) : undefined,
      segments: item.segments?.map((segment) => convertDimensionText(segment, c)),
    }),
  };
}

function summarizeOutcome(outcome: ItemOutcome): string {
  return outcome.message ?? "failed";
}

async function createDraftingView(
  client: RevitSender,
  spec: NonNullable<DrawDetailArgs["createView"]>,
  warnings: string[]
): Promise<{ viewId?: number; scale?: number; error?: string }> {
  const [outcome] = await runChunkedBatch(client, "create_view", "views", [
    definedOnly({ viewType: "Drafting", name: spec.name, scale: spec.scale, detailLevel: spec.detailLevel }),
  ]);
  const [viewId] = outcome.success ? readIds(outcome.data, "viewId") : [];
  if (viewId === undefined) return { error: `create_view failed: ${summarizeOutcome(outcome)}` };
  if (spec.titleOnSheet !== undefined) {
    const [titleOutcome] = await runChunkedBatch(client, "modify_annotations", "operations", [
      { action: "setParameters", elementIds: [viewId], parameters: { "Title on Sheet": spec.titleOnSheet } },
    ]);
    if (!titleOutcome.success) warnings.push(`Title on Sheet was not set: ${summarizeOutcome(titleOutcome)}`);
  }
  return { viewId, scale: spec.scale };
}

/** Runs the whole build against a connected Revit client. Exported for tests. */
export async function runDrawDetail(client: RevitSender, args: DrawDetailArgs): Promise<DrawDetailResult> {
  const result: DrawDetailResult = { created: {}, tags: {}, failures: [], warnings: [] };
  const units = args.units ?? "mm";

  let viewId = args.viewId;
  let scale = args.scale;
  if (args.createView) {
    const created = await createDraftingView(client, args.createView, result.warnings);
    if (created.error || created.viewId === undefined) {
      result.failures.push({ kind: "view", message: created.error ?? "create_view failed" });
      return result;
    }
    viewId = created.viewId;
    scale = scale ?? created.scale;
  }
  const needScale = units === "px" && scale === undefined;
  if (viewId === undefined || needScale) {
    try {
      const info = await resolveViewInfo(client, viewId, needScale);
      viewId = info.viewId;
      scale = scale ?? info.scale;
    } catch (error) {
      result.failures.push({ kind: "view", message: error instanceof Error ? error.message : String(error) });
      return result;
    }
  }
  result.viewId = viewId;

  let converter: DetailConverter;
  try {
    converter = createDetailConverter({
      units,
      origin: args.origin,
      pxPerPaperInch: args.pxPerPaperInch,
      scale,
      snapIn: args.snapIn,
    });
  } catch (error) {
    result.failures.push({ kind: "view", message: error instanceof Error ? error.message : String(error) });
    return result;
  }

  const tags: TagMap = new Map();
  const targetView = viewId as number;

  for (const kind of KINDS) {
    const items = (args[kind] ?? []) as Array<{ tag?: string }>;
    if (items.length === 0) continue;
    const { command, listKey, idField, perItemView } = COMMANDS[kind];

    const payloads: Record<string, unknown>[] = [];
    const sourceIndex: number[] = [];
    items.forEach((item, index) => {
      let built: Built;
      try {
        built = buildItem(kind, item, converter, targetView, tags);
      } catch (error) {
        built = { error: error instanceof Error ? error.message : String(error) };
      }
      built.warnings?.forEach((w) => result.warnings.push(`${kind}[${index}]: ${w}`));
      if (built.error || !built.payload) {
        result.failures.push(definedOnly({ kind, index, tag: item.tag, message: built.error ?? "invalid item" }));
        return;
      }
      payloads.push(built.payload);
      sourceIndex.push(index);
    });

    let createdCount = 0;
    if (payloads.length > 0) {
      const outcomes = await runChunkedBatch(
        client,
        command,
        listKey,
        payloads,
        perItemView ? {} : { viewId: targetView },
        CHUNK_SIZE
      );
      outcomes.forEach((outcome, position) => {
        const index = sourceIndex[position];
        const item = items[index];
        readWarnings(outcome.data).forEach((w) => result.warnings.push(`${kind}[${index}]: ${w}`));
        const ids = outcome.success ? readIds(outcome.data, idField) : [];
        if (!outcome.success || ids.length === 0) {
          result.failures.push(
            definedOnly({ kind, index, tag: item.tag, message: outcome.success ? "no element id returned" : summarizeOutcome(outcome) })
          );
          return;
        }
        createdCount++;
        addTagIds(tags, item.tag, ids);
      });
    }
    result.created[kind] = `${createdCount}/${items.length}`;
  }

  result.tags = Object.fromEntries(tags);
  return result;
}

function buildItem(kind: Kind, item: unknown, c: DetailConverter, viewId: number, tags: TagMap): Built {
  switch (kind) {
    case "regions":
      return buildRegion(item as z.infer<typeof regionSchema>, c);
    case "lines":
      return buildLine(item as LineItem, c);
    case "components":
      return buildComponent(item as ComponentItem, c);
    case "symbols":
      return buildSymbol(item as SymbolItem, c);
    case "texts":
      return buildText(item as TextItem, c, viewId);
    case "dimensions":
      return buildDimension(item as DimensionItem, c, viewId, tags);
  }
}

function createdTotal(result: DrawDetailResult): number {
  return Object.values(result.created).reduce((sum, value) => sum + Number(value?.split("/")[0] ?? 0), 0);
}

export function registerDrawDetailTool(server: McpServer) {
  server.tool(
    "draw_detail",
    "Draw a whole 2D detail in one call by composing create_view (Drafting), create_filled_region, create_detail_lines, place_detail_component, place_annotation_symbol, create_text_note and create_dimensions, in that order (regions -> lines -> components -> symbols -> texts -> dimensions), batched in chunks of 150. " +
      "Coordinates use `units`: mm/in/ft are real (model) sizes; px is image pixels with y pointing DOWN, converted as mm = (px - origin) / (pxPerPaperInch / scale) * 25.4 with y flipped (default pxPerPaperInch 166.667 = 7000 px across a 42\" sheet; scale from `scale`, createView.scale or the view). `origin` is the input point that maps to model (0,0). Radii and fillet radii use the same units; text `width` is paper mm (px mode: image px). Arc angles are degrees counter-clockwise as seen on paper in every mode. `snapIn` (e.g. 0.25) snaps converted points to that many real inches. " +
      "Give any element a `tag`; dimensions reference earlier elements by {tag, index?, end?} (index picks one curve of a poly; end: 'curve' for a line perpendicular to the dimension, 'start'/'end' for end points, 'center' etc. for components) or by {elementId, end?}; unknown tags fail that dimension only. " +
      'Example: {"createView":{"name":"FTG DETAIL","scale":12},"units":"in","lines":[{"kind":"poly","tag":"ftg","points":[{"x":0,"y":0},{"x":24,"y":0},{"x":24,"y":12},{"x":0,"y":12}],"closed":true,"lineStyle":"PEN5"}],"texts":[{"text":"#5 CONT.","at":{"x":30,"y":10},"type":"1/8\\" Arial","leaders":[{"end":{"x":12,"y":6}}]}],"dimensions":[{"refs":[{"tag":"ftg","index":3},{"tag":"ftg","index":1}],"through":{"x":0,"y":-6},"direction":"h","type":"Tick"}]}. ' +
      "Returns {viewId, created:{kind:'n/m'}, tags:{tag:[ids]}, failures:[{kind,index,tag,message}], warnings}; isError only when nothing was created.",
    drawDetailShape,
    async (args) => {
      if (args.viewId !== undefined && args.createView) {
        return {
          content: [{ type: "text" as const, text: "draw_detail: give either viewId or createView, not both." }],
          isError: true,
        };
      }
      const requested = KINDS.reduce((sum, kind) => sum + (args[kind]?.length ?? 0), 0);
      if (requested === 0 && !args.createView) {
        return {
          content: [{ type: "text" as const, text: "draw_detail: nothing to draw (give regions, lines, components, symbols, texts or dimensions)." }],
          isError: true,
        };
      }
      try {
        const result = await withRevitConnection((client) => runDrawDetail(client, args as DrawDetailArgs));
        const failedView = result.failures.some((f) => f.kind === "view");
        const nothingCreated = requested > 0 && createdTotal(result) === 0;
        return {
          content: [{ type: "text" as const, text: JSON.stringify(result, null, 2) }],
          ...(failedView || nothingCreated ? { isError: true } : {}),
        };
      } catch (error) {
        return {
          content: [
            { type: "text" as const, text: `draw_detail failed: ${error instanceof Error ? error.message : String(error)}` },
          ],
          isError: true,
        };
      }
    }
  );
}
