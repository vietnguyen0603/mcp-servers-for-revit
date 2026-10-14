import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { sendDocumentationCommand } from "../utils/documentationSchemas.js";

const parameterName = z.string().min(1).max(64);
const lengthMm = z.number().positive().max(500000);

const typeItem = z
  .object({
    name: z.string().min(1).max(256).describe("Type name, e.g. 'SGBR-01 1500x2800 L78000'"),
    width: lengthMm.optional().describe("Rectangular: Width in mm"),
    length: lengthMm.optional().describe("Rectangular: Length in mm"),
    diameter: lengthMm.optional().describe("Circular: Diameter in mm"),
    depth: lengthMm.optional().describe("Depth (pile length) in mm; ignored when depth is an instance parameter"),
    parameters: z
      .record(z.string().min(1).max(64), z.number().min(0).max(500000))
      .optional()
      .describe("Column/framing: section sizes in mm by key (d, bf, tw, tf, B, H, ...) or by parameter name, e.g. {d:900, bf:350}"),
  })
  .strict();

const sectionMm = z.number().positive().max(20000);

const section = z
  .object({
    d: sectionMm.optional().describe("I / cruciform: overall depth of the (first) I"),
    bf: sectionMm.optional().describe("I / cruciform: flange width"),
    tw: sectionMm.optional().describe("I / cruciform / box: web thickness"),
    tf: sectionMm.optional().describe("I / cruciform / box: flange thickness"),
    d2: sectionMm.optional().describe("Cruciform: depth of the second (crossing) I, default d"),
    bf2: sectionMm.optional().describe("Cruciform: flange width of the second I, default bf"),
    tw2: sectionMm.optional().describe("Cruciform: web thickness of the second I, default tw"),
    tf2: sectionMm.optional().describe("Cruciform: flange thickness of the second I, default tf"),
    B: sectionMm.optional().describe("Box: overall width (= flange plate width)"),
    H: sectionMm.optional().describe("Box: overall depth"),
    overhang: z
      .number()
      .min(0)
      .max(20000)
      .optional()
      .describe("Box: flange overhang beyond the outer face of each web (default 0 = closed box)"),
    b: sectionMm.optional().describe("Rect: width"),
    h: sectionMm.optional().describe("Rect: depth"),
    D: sectionMm.optional().describe("Circle / pipe: outside diameter"),
    t: sectionMm.optional().describe("Pipe: wall thickness"),
  })
  .strict();

/** Section keys per shape (mirrors SectionProfile.InputKeys in the add-in). */
export const SECTION_KEYS: Record<string, string[]> = {
  I: ["d", "bf", "tw", "tf"],
  cruciform: ["d", "bf", "tw", "tf", "d2", "bf2", "tw2", "tf2"],
  box: ["B", "H", "tw", "tf", "overhang"],
  rect: ["b", "h"],
  circle: ["D"],
  pipe: ["D", "t"],
  polygon: [],
};

const SECTION_SHAPES = ["I", "box", "cruciform", "rect", "circle", "pipe", "polygon"] as const;

/** Canonical section shape ('rectangular'/'circular' accepted, any case), or null. */
export function normalizeSectionShape(shape: string | undefined): string | null {
  if (!shape) return null;
  const s = shape.trim().toLowerCase();
  if (s === "rectangular") return "rect";
  if (s === "circular") return "circle";
  return SECTION_SHAPES.find((x) => x.toLowerCase() === s) ?? null;
}

const fmt = (x: number) => String(Math.round(x * 1000) / 1000);

/** Mirrors SectionProfile.Validate in the add-in (mm). */
export function validateSection(shape: string, values: Record<string, number>, points?: number[][]): string | null {
  if (shape === "polygon") {
    if (!points || points.length < 3) return "A polygon section needs at least 3 points.";
    let area = 0;
    for (let i = 0; i < points.length; i++) {
      const a = points[i];
      const b = points[(i + 1) % points.length];
      area += a[0] * b[1] - b[0] * a[1];
    }
    return Math.abs(area) < 1e-9 ? "The polygon points enclose no area." : null;
  }
  const v: Record<string, number> = { ...values };
  if (shape === "cruciform")
    for (const k of ["d", "bf", "tw", "tf"]) if (v[k + "2"] === undefined && v[k] !== undefined) v[k + "2"] = v[k];
  if (shape === "box" && v.overhang === undefined) v.overhang = 0;
  for (const key of SECTION_KEYS[shape]) {
    if (v[key] === undefined) return `Section '${shape}' needs '${key}'.`;
    if (key === "overhang" ? v[key] < 0 : v[key] <= 0) return `'${key}' must be positive.`;
  }
  const checkI = (s: string) => {
    const d = v["d" + s];
    const bf = v["bf" + s];
    const tw = v["tw" + s];
    const tf = v["tf" + s];
    if (tw >= bf) return `tw${s} (${fmt(tw)}) must be smaller than bf${s} (${fmt(bf)}).`;
    if (2 * tf >= d) return `2 x tf${s} (${fmt(2 * tf)}) must be smaller than d${s} (${fmt(d)}).`;
    return null;
  };
  switch (shape) {
    case "I":
      return checkI("");
    case "cruciform": {
      const error = checkI("") ?? checkI("2");
      if (error) return error;
      if (v.bf >= v.d2 - 2 * v.tf2)
        return `bf (${fmt(v.bf)}) must be smaller than d2 - 2 x tf2 (${fmt(v.d2 - 2 * v.tf2)}): the flanges of the two I sections would touch. Give the second I (d2, bf2, tw2, tf2) explicitly.`;
      if (v.bf2 >= v.d - 2 * v.tf)
        return `bf2 (${fmt(v.bf2)}) must be smaller than d - 2 x tf (${fmt(v.d - 2 * v.tf)}): the flanges of the two I sections would touch.`;
      return null;
    }
    case "box":
      if (2 * v.tf >= v.H) return `2 x tf (${fmt(2 * v.tf)}) must be smaller than H (${fmt(v.H)}).`;
      if (2 * (v.overhang + v.tw) >= v.B)
        return `2 x (overhang + tw) (${fmt(2 * (v.overhang + v.tw))}) must be smaller than B (${fmt(v.B)}).`;
      return null;
    case "pipe":
      return 2 * v.t >= v.D ? `2 x t (${fmt(2 * v.t)}) must be smaller than D (${fmt(v.D)}).` : null;
    default:
      return null;
  }
}

export const createFamilySchema = {
  name: z
    .string()
    .min(1)
    .max(120)
    .regex(/^[^\\/:*?"<>|]+$/, "Family name must be a valid file name")
    .describe("Family name (also the .rfa file name), e.g. 'MCP_Pile-Rectangular'"),
  kind: z
    .enum(["foundation", "genericModel", "column", "framing"])
    .optional()
    .describe(
      "Template: 'foundation' (Metric Structural Foundation, default - piles, barrettes, footings), 'genericModel', " +
        "'column' (Metric Structural Column - section in plan, ends locked to the base/top levels) or " +
        "'framing' (Metric Structural Framing - Beams and Braces - section in the end elevation, ends locked to Member Left/Right, section top on the Ref. Level - circle/pipe centred on it; the result's sectionTop is measured from the built solid)"
    ),
  shape: z
    .enum(["rectangular", "circular", "I", "box", "cruciform", "rect", "circle", "pipe", "polygon"])
    .optional()
    .describe(
      "Foundation/genericModel: 'rectangular' (default) or 'circular'. Column/framing (required): 'I', 'box' (built-up box girder, optional flange overhang), " +
        "'cruciform' (two welded I sections crossing), 'rect', 'circle', 'pipe' or 'polygon'"
    ),
  section: section
    .optional()
    .describe(
      "Column/framing section sizes in mm; they become type parameters named after the keys unless renamed in parameterNames. " +
        "I: d, bf, tw, tf. Cruciform: d, bf, tw, tf + optional second I d2, bf2, tw2, tf2 (crossing; its flanges must clear the first I's flanges). " +
        "Box: B, H, tw, tf, overhang. Rect: b, h. Circle: D. Pipe: D, t"
    ),
  points: z
    .array(z.array(z.number().min(-20000).max(20000)).length(2))
    .min(3)
    .max(200)
    .optional()
    .describe(
      "Polygon outline [[u,v],...] in mm. Column: plan x,y about the insertion point; framing: [across, up] about the placement line on the Ref. Level. Polygon families are never parametric"
    ),
  parametric: z
    .boolean()
    .optional()
    .describe(
      "Column/framing: true (default) = section planes driven by labelled type parameters so one family has many sizes; false = fixed geometry, one family per size. Falls back to fixed (with a warning) if the constraints fail"
    ),
  parameterNames: z
    .record(z.string().min(1).max(16), parameterName)
    .optional()
    .describe("Rename section parameters, e.g. {d:'Height', bf:'Flange Width'}"),
  material: z
    .string()
    .min(1)
    .max(128)
    .optional()
    .describe("Column/framing: Structural Material (default 'Steel' for I/box/cruciform/pipe, 'Concrete' otherwise; created if missing)"),
  areaParameter: z
    .union([parameterName, z.literal(false)])
    .optional()
    .describe("Column/framing: name of the section area parameter (a formula of the section parameters; default 'A'; false = none)"),
  sectionShape: z
    .string()
    .min(1)
    .max(64)
    .optional()
    .describe(
      "Column/framing: family Section Shape (Revit StructuralSectionShape name, e.g. 'IWideFlange', 'RectangleHSS', 'PipeStandard'); a failure only warns"
    ),
  direction: z
    .enum(["down", "up"])
    .optional()
    .describe("Extrusion direction from the insertion point (default down for foundation - the origin is the pile top - up for generic model)"),
  width: lengthMm.optional().describe("Default Width mm (rectangular, default 1200)"),
  length: lengthMm.optional().describe("Default Length mm (rectangular, default 2800)"),
  diameter: lengthMm.optional().describe("Default Diameter mm (circular, default 1000)"),
  depth: lengthMm.optional().describe("Default Depth mm (default 10000)"),
  instanceDepth: z
    .boolean()
    .optional()
    .describe("Make Depth an instance parameter (one type, pile length per instance); default false = type parameter"),
  widthParameter: parameterName.optional().describe("Name of the width parameter (default 'Width')"),
  lengthParameter: parameterName.optional().describe("Name of the length parameter (default 'Length')"),
  diameterParameter: parameterName.optional().describe("Name of the diameter parameter (default 'Diameter')"),
  depthParameter: parameterName.optional().describe("Name of the depth parameter (default 'Depth')"),
  templatePath: z.string().min(1).max(400).optional().describe("Explicit .rft template path (default found in the Revit template folders)"),
  saveFolder: z
    .string()
    .min(1)
    .max(400)
    .optional()
    .describe("Folder for the generated .rfa (default %APPDATA%\\revit-mcp\\families) - build an office library this way"),
  overwrite: z.boolean().optional().describe("Rebuild and reload when a family with this name is already in the project"),
  types: z.array(typeItem).max(500).optional().describe("Types to create or update after loading"),
};

const schemaObject = z.object(createFamilySchema).strict();
export type CreateFamilyArgs = z.infer<typeof schemaObject>;

const SECTION_ONLY = ["section", "points", "parametric", "parameterNames", "material", "areaParameter", "sectionShape"] as const;
const LEGACY_ONLY = ["width", "length", "diameter", "depth", "instanceDepth", "direction"] as const;

export function validateCreateFamily(args: CreateFamilyArgs): string | null {
  if (args.kind === "column" || args.kind === "framing") return validateSectionFamily(args);
  if (args.shape && args.shape !== "rectangular" && args.shape !== "circular")
    return `shape '${args.shape}' needs kind 'column' or 'framing'; foundation/genericModel take 'rectangular' or 'circular'`;
  for (const key of SECTION_ONLY) if (args[key] !== undefined) return `'${key}' is only for kind 'column' or 'framing'`;
  if ((args.types ?? []).some((t) => t.parameters !== undefined)) return "Type 'parameters' are only for kind 'column' or 'framing'";

  const circular = args.shape === "circular";
  for (const type of args.types ?? []) {
    if (circular && (type.width !== undefined || type.length !== undefined))
      return `Type '${type.name}': circular families take diameter, not width/length`;
    if (!circular && type.diameter !== undefined) return `Type '${type.name}': rectangular families take width/length, not diameter`;
  }
  if (circular && (args.width !== undefined || args.length !== undefined)) return "Circular families take diameter, not width/length";
  if (!circular && args.diameter !== undefined) return "Rectangular families take width/length, not diameter";
  return null;
}

function validateSectionFamily(args: CreateFamilyArgs): string | null {
  const shape = normalizeSectionShape(args.shape);
  if (!shape) return "Columns and framing need shape 'I', 'box', 'cruciform', 'rect', 'circle', 'pipe' or 'polygon'";
  for (const key of LEGACY_ONLY)
    if (args[key] !== undefined) return `'${key}' is not used by kind '${args.kind}'; give the sizes in 'section'`;
  const values = { ...(args.section ?? {}) } as Record<string, number>;
  const allowed = SECTION_KEYS[shape];
  for (const key of Object.keys(values))
    if (!allowed.includes(key)) return `Section '${shape}' does not take '${key}' (use ${allowed.join(", ") || "points"})`;
  if (shape !== "polygon" && args.points) return "'points' is only for shape 'polygon'";
  const error = validateSection(shape, values, args.points);
  if (error) return error;

  const names = args.parameterNames ?? {};
  for (const key of Object.keys(names)) if (!allowed.includes(key)) return `parameterNames: '${key}' is not a key of section '${shape}'`;
  const keyOf = (name: string) => allowed.find((k) => k === name || names[k] === name);
  const parametric = shape !== "polygon" && args.parametric !== false;
  for (const type of args.types ?? []) {
    if (type.width !== undefined || type.length !== undefined || type.diameter !== undefined || type.depth !== undefined)
      return `Type '${type.name}': column/framing types take 'parameters', not width/length/diameter/depth`;
    if (!type.parameters) continue;
    if (!parametric)
      return `Type '${type.name}': a ${shape === "polygon" ? "polygon" : "parametric:false"} family has fixed geometry; build one family per size instead of type parameters`;
    const merged = { ...values };
    for (const [name, value] of Object.entries(type.parameters)) {
      const key = keyOf(name);
      if (key) merged[key] = value;
    }
    const typeError = validateSection(shape, merged);
    if (typeError) return `Type '${type.name}': ${typeError}`;
  }
  return null;
}

export function registerCreateFamilyTool(server: McpServer) {
  server.tool(
    "create_family",
    "Build a parametric family (.rfa) from a Revit family template when no suitable family exists, load it and create sized types - e.g. a rectangular barrette pile (Width x Length x Depth) or a round bored pile (Diameter x Depth). " +
      "The plan profile is locked to reference planes driven by labelled dimensions (symmetric about the centre planes) and the extrusion end is driven by Depth, so types flex like a hand-made family; a flex test runs before saving and is reported. " +
      "Foundation families extrude down from the insertion point (insertion point = pile top), so place them with create_foundations kind:'isolated' and topOffset, or kind:'pile' with length. " +
      "kind:'column' / 'framing' builds steel or concrete members with standard or built-up sections (I, box girder with optional flange overhang, cruciform of two welded I, rect, circle, pipe, polygon): " +
      "section sizes are labelled type parameters (one family, many sizes; set them via types[].parameters or create_family_type), the ends follow the column levels / beam length, " +
      "Structural Material is associated and a section area parameter (formula) is added; parametric:false gives fixed geometry (one family per size). " +
      "The .rfa is saved to saveFolder (default %APPDATA%\\revit-mcp\\families) for reuse via load_family. Existing family with the same name is reused unless overwrite:true. All lengths mm.",
    createFamilySchema,
    async (args) => {
      const error = validateCreateFamily(args as CreateFamilyArgs);
      if (error) return { content: [{ type: "text" as const, text: `create_family failed: ${error}` }], isError: true };
      return sendDocumentationCommand("create_family", args, 330000);
    }
  );
}
