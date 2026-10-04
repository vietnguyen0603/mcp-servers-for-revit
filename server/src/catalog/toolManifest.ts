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
  capture_view: {
    catalogs: ["core", "views"],
    readOnly: true,
    keywords: ["screenshot", "image", "zoom to fit", "refresh", "preview", "check result"],
  },

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
  create_family_type: {
    catalogs: ["structure", "architecture"],
    readOnly: false,
    keywords: ["type", "duplicate", "section", "size", "family type", "steel section", "thickness", "footing size"],
  },
  load_family: {
    catalogs: ["structure", "architecture"],
    readOnly: false,
    keywords: ["load", "family", "rfa", "library", "pile cap", "column family", "beam family"],
  },
  create_slabs: {
    catalogs: ["structure", "architecture"],
    readOnly: false,
    keywords: ["floor", "slab", "opening", "shaft", "step", "drop", "foundation slab", "raft", "basement slab", "bulk", "csv"],
  },
  create_foundations: {
    catalogs: ["structure"],
    readOnly: false,
    keywords: ["footing", "pile", "pile cap", "isolated footing", "foundation", "bulk", "csv", "hexagonal cap"],
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
  tag_elements: {
    catalogs: ["annotate"],
    readOnly: false,
    keywords: ["tag", "label", "beam", "column", "door", "window", "untagged", "drafting"],
  },
  create_text_note: { catalogs: ["annotate"], readOnly: false, keywords: ["text", "note", "label", "drafting", "leader"] },
  get_view_annotations: {
    catalogs: ["annotate", "views"],
    readOnly: true,
    keywords: ["read", "list", "detail line", "text", "dimension", "filled region", "drafting", "2d"],
  },
  modify_annotations: {
    catalogs: ["annotate"],
    readOnly: false,
    destructive: true,
    keywords: ["edit", "move", "copy", "rotate", "delete", "text", "line style", "drafting", "2d", "leader", "dimension text", "override", "mirror", "flip"],
  },
  create_detail_lines: {
    catalogs: ["annotate"],
    readOnly: false,
    keywords: ["detail line", "detail curve", "arc", "line style", "drafting"],
  },
  array_annotations: {
    catalogs: ["annotate"],
    readOnly: false,
    keywords: ["array", "repeat", "spacing", "o.c.", "on center", "pattern", "copy", "rebar dots", "nails", "screws", "drafting"],
  },
  list_detail_groups: {
    catalogs: ["annotate"],
    readOnly: true,
    keywords: ["detail group", "group type", "typical", "symbol", "library", "drafting"],
  },
  modify_detail_groups: {
    catalogs: ["annotate"],
    readOnly: false,
    keywords: ["detail group", "place group", "create group", "ungroup", "typical", "symbol", "drafting"],
  },
  create_filled_region: {
    catalogs: ["annotate"],
    readOnly: false,
    keywords: ["filled region", "hatch", "pattern", "masking region", "drafting"],
  },
  place_detail_component: {
    catalogs: ["annotate"],
    readOnly: false,
    keywords: ["detail component", "detail item", "repeating detail", "drafting"],
  },
  place_annotation_symbol: {
    catalogs: ["annotate"],
    readOnly: false,
    keywords: [
      "generic annotation",
      "symbol",
      "subtitle",
      "sub-detail title",
      "weld symbol",
      "elevation marker",
      "leader",
      "drafting",
    ],
  },
  draw_detail: {
    catalogs: ["annotate"],
    readOnly: false,
    keywords: [
      "typical detail",
      "build",
      "redraw",
      "image",
      "pixels",
      "inches",
      "tag",
      "batch",
      "drafting view",
      "detail line",
      "filled region",
      "dimension",
      "text",
      "drafting",
    ],
  },
  create_drafted_table: {
    catalogs: ["annotate"],
    readOnly: false,
    keywords: ["table", "schedule", "grid", "merged cells", "rebar schedule", "drafted schedule", "drafting"],
  },
  create_revision_cloud: { catalogs: ["annotate"], readOnly: false, keywords: ["revision", "cloud", "markup"] },
  create_grid_dimensions: {
    catalogs: ["annotate", "structure"],
    readOnly: false,
    keywords: ["dimension", "grid", "chain", "overall", "axis", "drafting"],
  },
  create_spot_elevations: {
    catalogs: ["annotate", "structure"],
    readOnly: false,
    keywords: ["spot elevation", "spot coordinate", "level", "top of beam", "drafting"],
  },
  find_tag_overlaps: { catalogs: ["annotate"], readOnly: true, keywords: ["tag", "overlap", "clash", "cleanup", "qa"] },
  delete_orphaned_tags: {
    catalogs: ["annotate"],
    readOnly: false,
    destructive: true,
    keywords: ["tag", "orphan", "empty", "cleanup"],
  },
  create_rebar_annotation: {
    catalogs: ["annotate", "structure"],
    readOnly: false,
    keywords: ["rebar", "multi-rebar", "reinforcement", "tag", "dimension"],
  },
  list_drafting_types: {
    catalogs: ["annotate", "views"],
    readOnly: true,
    keywords: ["text type", "dimension type", "filled region type", "line style", "viewport type", "title block", "standards", "drafting"],
  },

  // views / sheets
  list_views: {
    catalogs: ["views"],
    readOnly: true,
    keywords: ["view template", "view family type", "plan", "section", "elevation", "placed"],
  },
  list_sheets: { catalogs: ["views"], readOnly: true, keywords: ["sheet", "titleblock", "title block", "viewport"] },
  create_view: {
    catalogs: ["views"],
    readOnly: false,
    keywords: ["plan", "floor plan", "structural plan", "ceiling", "section", "elevation", "3d", "drafting view"],
  },
  duplicate_view: { catalogs: ["views"], readOnly: false, keywords: ["copy", "dependent", "detailing"] },
  create_sheet: { catalogs: ["views"], readOnly: false, keywords: ["sheet", "titleblock", "title block", "revision"] },
  place_viewport: { catalogs: ["views"], readOnly: false, keywords: ["viewport", "sheet", "place view", "layout"] },
  update_viewports: {
    catalogs: ["views"],
    readOnly: false,
    keywords: ["viewport", "detail number", "renumber", "anchor", "align", "viewport type", "title", "label", "sheet"],
  },
  create_schedule: {
    catalogs: ["views", "analyze"],
    readOnly: false,
    keywords: ["schedule", "quantities", "table", "fields", "filter"],
  },
  get_schedule_data: {
    catalogs: ["views", "analyze"],
    readOnly: true,
    keywords: ["schedule", "table", "rows", "export"],
  },
  apply_view_template: { catalogs: ["views"], readOnly: false, keywords: ["view template", "template", "standards"] },
  create_view_filter: {
    catalogs: ["views"],
    readOnly: false,
    keywords: ["filter", "visibility", "graphics", "parameter filter", "rule"],
  },
  override_graphics: {
    catalogs: ["views", "modify"],
    readOnly: false,
    keywords: ["override", "visibility", "graphics", "halftone", "line weight", "pattern", "category"],
  },
  export_sheets: { catalogs: ["views"], readOnly: false, keywords: ["pdf", "dwg", "print", "publish", "export"] },
  export_view_image: { catalogs: ["views"], readOnly: false, keywords: ["image", "png", "jpg", "snapshot", "export"] },
  crop_image_grid: {
    catalogs: ["views"],
    readOnly: true,
    keywords: ["crop", "grid", "pixel", "coordinates", "zoom", "png", "sheet image", "redraw", "trace"],
  },
  overlay_images: {
    catalogs: ["views"],
    readOnly: true,
    keywords: ["overlay", "compare", "diff", "difference", "rebuilt", "original", "misplaced", "png"],
  },
  image_info: {
    catalogs: ["views"],
    readOnly: true,
    keywords: ["image size", "pixels", "dpi", "px per inch", "png", "sheet export"],
  },
  list_revisions: { catalogs: ["views"], readOnly: true, keywords: ["revision", "sequence", "issue"] },
  create_revision: { catalogs: ["views"], readOnly: false, keywords: ["revision", "issue", "sequence"] },
  update_sheets: {
    catalogs: ["views"],
    readOnly: false,
    keywords: ["sheet", "rename", "renumber", "titleblock", "title block", "revision", "parameter"],
  },
  create_callout: { catalogs: ["views"], readOnly: false, keywords: ["callout", "detail view", "enlarged"] },
  set_crop_region: { catalogs: ["views"], readOnly: false, keywords: ["crop", "crop box", "annotation crop", "extent"] },
  set_view_range: { catalogs: ["views"], readOnly: false, keywords: ["view range", "cut plane", "view depth", "plan"] },
  align_viewports: { catalogs: ["views"], readOnly: false, keywords: ["viewport", "align", "sheet", "layout"] },

  // detail library / sheet workflows
  audit_detail_references: {
    catalogs: ["annotate", "views"],
    readOnly: true,
    keywords: ["section cut", "bubble", "detail number", "sheet number", "view reference", "dead", "qa", "library"],
  },
  sync_detail_references: {
    catalogs: ["annotate"],
    readOnly: false,
    keywords: ["section cut", "bubble", "detail number", "sheet number", "renumber", "fix references", "library"],
  },
  create_view_reference: {
    catalogs: ["annotate", "views"],
    readOnly: false,
    keywords: ["reference callout", "reference section", "view reference", "live reference", "drafting"],
  },
  copy_view_contents: {
    catalogs: ["views", "annotate"],
    readOnly: false,
    keywords: ["copy", "paste", "transfer", "library", "drafting", "detail", "other document"],
  },
  copy_drafting_views: {
    catalogs: ["views"],
    readOnly: false,
    keywords: ["insert views from file", "import", "library", "typical detail", "drafting view", "transfer"],
  },
  layout_detail_sheet: {
    catalogs: ["views"],
    readOnly: false,
    keywords: ["sheet", "layout", "grid", "module", "detail number", "viewport", "typical detail", "pack"],
  },

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
