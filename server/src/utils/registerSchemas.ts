import { z } from "zod";

/**
 * Shared request contract for the structural register extraction tools
 * (`get_grid_register_data`, `get_column_wall_register_data`,
 * `get_beam_register_data`).
 *
 * The Revit add-in reads each JSON-RPC request from a single 8 KB socket
 * read, so every request must stay compact. This module owns the reusable
 * Zod fragments, the resolved defaults, and the request-size guard so the
 * three tool facades cannot drift apart.
 *
 * All lengths are millimetres (mm) and every response row is one record per
 * physical element instance, keyed by `UniqueId`.
 */

/** Maximum size in bytes of a complete JSON-RPC request envelope. */
export const REGISTER_REQUEST_MAX_BYTES = 8 * 1024;

export const DEFAULT_PAGE_SIZE = 100;
export const MAX_PAGE_SIZE = 500;

export const DESIGN_OPTION_POLICIES = ["primary", "active", "all"] as const;
export const COORDINATE_SYSTEMS = ["project", "shared"] as const;
export const SUPPORT_CATEGORIES = ["wall", "column", "beam"] as const;

export type DesignOptionPolicy = (typeof DESIGN_OPTION_POLICIES)[number];
export type CoordinateSystem = (typeof COORDINATE_SYSTEMS)[number];
export type SupportCategory = (typeof SUPPORT_CATEGORIES)[number];

/** Raised for request options that are invalid or produce an oversized request. */
export class RegisterOptionError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "RegisterOptionError";
  }
}

const levelNameString = z.string().min(1).max(128);
const boundedNameList = z.array(levelNameString).max(256);

/**
 * Positive tolerance bounds, in millimetres unless stated otherwise. Applied
 * only to derivation/presentation; geometry keeps full precision internally.
 */
export const toleranceShape = {
  angularDegrees: z
    .number()
    .positive()
    .max(90)
    .default(0.5)
    .describe(
      "Angular tolerance in degrees used to cluster grid/beam directions and detect parallel faces. Defaults to 0.5 degrees."
    ),
  intersectionMm: z
    .number()
    .positive()
    .max(1000)
    .default(5)
    .describe(
      "Maximum gap in millimetres still treated as a geometric intersection between beam axes and support faces. Defaults to 5 mm."
    ),
  groupingMm: z
    .number()
    .positive()
    .max(1000)
    .default(10)
    .describe(
      "Spatial connectivity distance in millimetres used when grouping multi-leg walls without explicit model grouping. Defaults to 10 mm."
    ),
  supportSearchMm: z
    .number()
    .positive()
    .max(10000)
    .default(500)
    .describe(
      "Search radius in millimetres around each beam end when pruning candidate support solids. Defaults to 500 mm."
    ),
  snapMm: z
    .number()
    .positive()
    .max(1000)
    .default(1)
    .describe(
      "Presentation snap in millimetres applied only to register-ready fields, never to geometry. Defaults to 1 mm."
    ),
};

export const tolerancesSchema = z.object(toleranceShape);

export type RegisterTolerances = z.infer<typeof tolerancesSchema>;
export type RegisterTolerancesInput = z.input<typeof tolerancesSchema>;

/** Resolved default tolerances, derived from the schema so they cannot drift. */
export const DEFAULT_TOLERANCES: RegisterTolerances = tolerancesSchema.parse({});

/**
 * Input fields shared by all three register tools. Spread this object into a
 * tool's input shape (for example `{ ...sharedRegisterInputShape, ... }`).
 */
export const sharedRegisterInputShape = {
  levelIds: boundedNameList
    .optional()
    .describe(
      "Optional list of Revit level element IDs to restrict the extraction to. An empty or omitted list means all levels in scope. At most 256 IDs."
    ),
  levelNames: boundedNameList
    .optional()
    .describe(
      "Optional list of Revit level names to restrict the extraction to. At most 256 names; ambiguous names are rejected rather than guessed."
    ),
  viewId: z
    .string()
    .min(1)
    .max(128)
    .optional()
    .describe(
      "Optional view element ID to scope the extraction to that view. When omitted, model elements are collected document-wide."
    ),
  phaseId: z
    .string()
    .min(1)
    .max(128)
    .optional()
    .describe(
      "Optional phase element ID. Defaults to the active view phase when viewId is set, otherwise the document default phase is reported in the snapshot."
    ),
  designOptionPolicy: z
    .enum(DESIGN_OPTION_POLICIES)
    .default("primary")
    .describe(
      "Design-option scope: 'primary' (default), 'active' (requires viewId), or 'all'."
    ),
  includeLinkedModels: z
    .boolean()
    .default(false)
    .describe(
      "When true, include instances from loaded Revit links, keyed by link instance identity plus UniqueId. Defaults to false."
    ),
  coordinateSystem: z
    .enum(COORDINATE_SYSTEMS)
    .default("project")
    .describe(
      "Output coordinate frame: 'project' (default) or 'shared'. The selected transform is reported in the snapshot."
    ),
  pageSize: z
    .number()
    .int()
    .positive()
    .max(MAX_PAGE_SIZE)
    .default(DEFAULT_PAGE_SIZE)
    .describe(
      `Maximum number of records returned per page, from 1 to ${MAX_PAGE_SIZE}. Defaults to ${DEFAULT_PAGE_SIZE}. Fetch pages sequentially with cursor.`
    ),
  cursor: z
    .string()
    .min(1)
    .max(1024)
    .optional()
    .describe(
      "Opaque cursor from a previous response's page.nextCursor. Fetch pages sequentially; do not dispatch concurrent page requests over the shared socket."
    ),
  includeEvidence: z
    .boolean()
    .default(true)
    .describe(
      "When true (default), each record includes compact derivation evidence and diagnostics. Set false for smaller payloads that only need register rows."
    ),
  tolerances: tolerancesSchema
    .default({})
    .describe(
      "Optional tolerance overrides in millimetres (angularDegrees in degrees). Missing values fall back to the documented defaults."
    ),
  parameterMap: z
    .record(
      z.string().min(1).max(64),
      z.array(z.string().min(1).max(64)).max(20)
    )
    .refine((map) => Object.keys(map).length <= 32, {
      message: "parameterMap may contain at most 32 keys",
    })
    .optional()
    .describe(
      "Optional ordered parameter aliases, for example { \"mark\": [\"Mark\", \"Nhan\"] }. At most 32 keys and 20 aliases per key; each key/alias is at most 64 characters. Built-in parameters are always attempted first."
    ),
};

export type SharedRegisterInput = {
  levelIds?: string[];
  levelNames?: string[];
  viewId?: string;
  phaseId?: string;
  designOptionPolicy?: DesignOptionPolicy;
  includeLinkedModels?: boolean;
  coordinateSystem?: CoordinateSystem;
  pageSize?: number;
  cursor?: string;
  includeEvidence?: boolean;
  tolerances?: RegisterTolerancesInput;
  parameterMap?: Record<string, string[]>;
};

export interface SharedRegisterParams {
  levelIds: string[];
  levelNames: string[];
  pageSize: number;
  designOptionPolicy: DesignOptionPolicy;
  includeLinkedModels: boolean;
  coordinateSystem: CoordinateSystem;
  includeEvidence: boolean;
  tolerances: RegisterTolerances;
  parameterMap: Record<string, string[]>;
  viewId?: string;
  phaseId?: string;
  cursor?: string;
}

/**
 * Resolve shared request options to their documented defaults and reject
 * combinations that cannot be honoured. Throws {@link RegisterOptionError}.
 */
export function buildSharedRegisterParams(
  args: SharedRegisterInput
): SharedRegisterParams {
  const params: SharedRegisterParams = {
    levelIds: args.levelIds ?? [],
    levelNames: args.levelNames ?? [],
    pageSize: args.pageSize ?? DEFAULT_PAGE_SIZE,
    designOptionPolicy: args.designOptionPolicy ?? "primary",
    includeLinkedModels: args.includeLinkedModels ?? false,
    coordinateSystem: args.coordinateSystem ?? "project",
    includeEvidence: args.includeEvidence ?? true,
    tolerances: tolerancesSchema.parse(args.tolerances ?? {}),
    parameterMap: args.parameterMap ?? {},
  };

  if (args.viewId !== undefined) {
    params.viewId = args.viewId;
  }
  if (args.phaseId !== undefined) {
    params.phaseId = args.phaseId;
  }
  if (args.cursor !== undefined) {
    params.cursor = args.cursor;
  }

  if (params.designOptionPolicy === "active" && params.viewId === undefined) {
    throw new RegisterOptionError(
      'designOptionPolicy "active" requires viewId because the active design option is view-scoped.'
    );
  }

  return params;
}

/**
 * Guard that the complete serialized JSON-RPC request for `method` fits the
 * add-in's single 8 KB read. Returns the measured byte length.
 * Throws {@link RegisterOptionError} when the request would be truncated.
 */
export function assertRegisterRequestSize(
  method: string,
  params: unknown
): number {
  const envelope = JSON.stringify({
    jsonrpc: "2.0",
    method,
    params,
    id: "0000000000",
  });
  const bytes = Buffer.byteLength(envelope, "utf8");

  if (bytes >= REGISTER_REQUEST_MAX_BYTES) {
    throw new RegisterOptionError(
      `Serialized "${method}" request is ${bytes} bytes, which exceeds the ${REGISTER_REQUEST_MAX_BYTES}-byte single-read limit. Reduce level lists, parameter aliases, or pageSize.`
    );
  }

  return bytes;
}
