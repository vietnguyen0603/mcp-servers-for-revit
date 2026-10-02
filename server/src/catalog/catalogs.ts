/**
 * Tool catalogs, modelled on the Revit ribbon tabs so that users and models
 * can reason about them with familiar vocabulary.
 *
 * `core` is layer 1: always exposed. Every other catalog is layer 2 and is
 * exposed on demand through `enable_catalog`.
 */
export interface CatalogDefinition {
  id: string;
  title: string;
  description: string;
  /** Alternative names accepted by `enable_catalog` and matched by `search_tools`. */
  aliases: string[];
}

export const CORE_CATALOG = "core";

export const CATALOGS: readonly CatalogDefinition[] = [
  {
    id: CORE_CATALOG,
    title: "Core",
    description: "Always available: active view, selection, element queries and family types.",
    aliases: ["general", "query", "view"],
  },
  {
    id: "architecture",
    title: "Architecture",
    description: "Model architectural elements: walls, doors, windows, floors, roofs, rooms, levels and grids.",
    aliases: ["arch", "architectural", "room", "rooms"],
  },
  {
    id: "structure",
    title: "Structure",
    description: "Structural modelling and register extraction: framing systems, beams, columns, structural walls, grids and levels.",
    aliases: ["struct", "structural", "register", "framing"],
  },
  {
    id: "annotate",
    title: "Annotate / Drafting",
    description:
      "Annotation in a view: dimensions, grid dimensions, spot elevations, tags, text notes, detail lines, filled regions, detail components, revision clouds, rebar annotation and tag cleanup.",
    aliases: ["drafting", "annotation", "documentation", "tag", "tags", "dimension", "dimensions", "text"],
  },
  {
    id: "views",
    title: "View / Sheets",
    description:
      "Views, sheets, viewports and schedules: create and list them, place and align views on sheets, templates, filters, graphic overrides, crop and view range, revisions, and PDF/DWG/image export.",
    aliases: [
      "sheet",
      "sheets",
      "viewport",
      "viewports",
      "titleblock",
      "schedule",
      "schedules",
      "section",
      "elevation",
      "export",
      "pdf",
      "revision",
      "revisions",
    ],
  },
  {
    id: "modify",
    title: "Modify",
    description: "Change existing elements: select, hide, isolate, recolor, set transparency and delete.",
    aliases: ["edit", "operate", "delete"],
  },
  {
    id: "analyze",
    title: "Analyze",
    description: "Model statistics, material quantities and room data exports.",
    aliases: ["analysis", "quantities", "quantity", "takeoff", "statistics"],
  },
  {
    id: "data",
    title: "Data",
    description: "Store and query Revit project and room data in the local SQLite database.",
    aliases: ["database", "storage", "store", "sqlite"],
  },
  {
    id: "automation",
    title: "Automation",
    description: "Run arbitrary C# code inside Revit when no dedicated tool exists.",
    aliases: ["code", "script", "macro", "csharp", "api"],
  },
];

const CATALOG_BY_KEY = new Map<string, CatalogDefinition>();
for (const catalog of CATALOGS) {
  CATALOG_BY_KEY.set(catalog.id, catalog);
  for (const alias of catalog.aliases) {
    CATALOG_BY_KEY.set(alias, catalog);
  }
}

/** Resolve a catalog id or alias (case-insensitive) to its definition. */
export function resolveCatalog(name: string): CatalogDefinition | undefined {
  return CATALOG_BY_KEY.get(name.trim().toLowerCase());
}
