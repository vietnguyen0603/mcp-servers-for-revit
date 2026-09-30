/**
 * Catalog placement and safety metadata for every tool in `src/tools/`.
 *
 * - `catalogs`: first entry is the primary catalog; a tool may appear in
 *   several catalogs and is enabled when any of them is enabled.
 * - `readOnly`: the tool does not change the Revit model or local database.
 *   Only read-only tools may be invoked through the `call_tool` proxy.
 * - `destructive`: the tool may delete or irreversibly change model content.
 * - `keywords`: extra search terms that do not appear in the description.
 *
 * Every tool registered from `src/tools/` must have an entry here; the tool
 * catalog test enforces this.
 */
export interface ToolManifestEntry {
  catalogs: string[];
  readOnly: boolean;
  destructive?: boolean;
  keywords?: string[];
}

export const TOOL_MANIFEST: Record<string, ToolManifestEntry> = {
  // core (layer 1)
  say_hello: { catalogs: ["core"], readOnly: true, keywords: ["ping", "connection", "test"] },
  get_current_view_info: { catalogs: ["core"], readOnly: true, keywords: ["active view", "scale"] },
  get_current_view_elements: { catalogs: ["core"], readOnly: true },
  get_selected_elements: { catalogs: ["core"], readOnly: true, keywords: ["selection"] },
  get_available_family_types: { catalogs: ["core"], readOnly: true, keywords: ["family", "type", "symbol"] },
  ai_element_filter: { catalogs: ["core"], readOnly: true, keywords: ["find", "filter", "search", "elements"] },

  // architecture / structure modelling
  create_point_based_element: {
    catalogs: ["architecture", "structure"],
    readOnly: false,
    keywords: ["door", "window", "furniture", "column", "place", "family instance"],
  },
  create_line_based_element: {
    catalogs: ["architecture", "structure"],
    readOnly: false,
    keywords: ["wall", "beam", "pipe", "duct"],
  },
  create_surface_based_element: {
    catalogs: ["architecture", "structure"],
    readOnly: false,
    keywords: ["floor", "slab", "ceiling", "roof"],
  },
  create_level: { catalogs: ["architecture", "structure"], readOnly: false, keywords: ["datum", "elevation", "storey"] },
  create_grid: { catalogs: ["architecture", "structure"], readOnly: false, keywords: ["datum", "axis", "gridline"] },
  create_room: { catalogs: ["architecture"], readOnly: false, keywords: ["space"] },
  create_structural_framing_system: {
    catalogs: ["structure"],
    readOnly: false,
    keywords: ["beam system", "joist", "framing"],
  },
  get_grid_register_data: { catalogs: ["structure"], readOnly: true, keywords: ["register", "schedule", "axis"] },
  get_column_wall_register_data: {
    catalogs: ["structure"],
    readOnly: true,
    keywords: ["register", "schedule", "column", "wall", "pier"],
  },
  get_beam_register_data: {
    catalogs: ["structure"],
    readOnly: true,
    keywords: ["register", "schedule", "beam", "span", "support"],
  },

  // annotate / drafting
  create_dimensions: { catalogs: ["annotate"], readOnly: false, keywords: ["dimension", "drafting"] },
  tag_all_walls: { catalogs: ["annotate"], readOnly: false, keywords: ["tag", "wall", "drafting"] },
  tag_all_rooms: { catalogs: ["annotate"], readOnly: false, keywords: ["tag", "room", "drafting"] },

  // modify
  operate_element: {
    catalogs: ["modify"],
    readOnly: false,
    destructive: true,
    keywords: ["select", "hide", "isolate", "transparency", "color"],
  },
  color_elements: { catalogs: ["modify"], readOnly: false, keywords: ["colour", "color", "override", "visualize"] },
  delete_element: { catalogs: ["modify"], readOnly: false, destructive: true, keywords: ["remove"] },

  // analyze
  analyze_model_statistics: { catalogs: ["analyze"], readOnly: true, keywords: ["count", "statistics", "health"] },
  get_material_quantities: { catalogs: ["analyze"], readOnly: true, keywords: ["material", "takeoff", "quantity"] },
  export_room_data: { catalogs: ["analyze", "architecture"], readOnly: true, keywords: ["room", "area", "schedule"] },

  // data
  store_project_data: { catalogs: ["data"], readOnly: false, keywords: ["save", "project"] },
  store_room_data: { catalogs: ["data"], readOnly: false, keywords: ["save", "room"] },
  query_stored_data: { catalogs: ["data"], readOnly: true, keywords: ["database", "project", "room"] },

  // automation
  send_code_to_revit: {
    catalogs: ["automation"],
    readOnly: false,
    destructive: true,
    keywords: ["csharp", "c#", "script", "execute", "api"],
  },
};
