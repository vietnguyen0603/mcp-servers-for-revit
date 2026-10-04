# Drafting tool gaps — typical-detail workflow

Source: live profiling of `TT-WR-TYPICAL-DETAILS_R25` (923 drafting views, 901
on 104 sheets, ~132k view-specific elements), a pilot drawing session using only
the existing MCP tools, and live verification of the workarounds (Revit 2025,
2026-10-03). Skills that reference these ids: `.claude/skills/revit-*`.

## What the drafting work actually consists of

| Element kind (in drafting views) | Count | Views using it | Tool today |
|---|---|---|---|
| Detail lines / arcs (`Lines`) | 52,000 | 900 | `create_detail_lines` ✅ |
| Detail items (families) | 17,052 | 743 | `place_detail_component` ✅ (no mirror, no array) |
| Text notes | 15,758 | 915 | `create_text_note` ⚠ no leaders |
| ↳ with leaders | 5,356 notes / 5,886 leaders | — | ❌ G1 |
| Dimensions | 2,895 | 579 | `create_dimensions` ⚠ G3, G4 |
| ↳ value overrides / prefix-suffix-below | 1,916 / 1,103 | — | ❌ G3 |
| Filled regions | 2,875 | 471 | `create_filled_region` ✅ |
| Detail groups | 2,442 (307 types) | 244 | ❌ G6 |
| Generic annotations (subtitle, weld, elev marker, grid head) | 2,162 | 607 | ❌ G2 |
| Manual reference bubbles (`Section Cut*` detail items) | 974 | — | place ✅ / audit ❌ G10 |
| View References (live) | 105 | 43 | ❌ G10 |
| CAD imports | 64 | 19 | ❌ G17 |

Typical view: median 89 elements (p90 327, max 1,621). Scales 1:12 > 1:8 > 1:16 > 1:1.

## Gap register (prioritised)

### P0 — blocks producing a normal-looking detail

| Id | Gap | Evidence | Proposed tool / change | Verified workaround |
|---|---|---|---|---|
| **G1** | Text-note leaders | 34% of notes have leaders | extend `create_text_note` with `leaders:[{ end, elbow?, side:"L"/"R", shape:"Straight"/"Arc" }]`; `modify_annotations` actions `addLeader` / `removeLeaders` / `setLeader` | `TextNote.AddLeader` + set `End`/`Elbow` ✅ |
| **G2** | Place generic annotations (+ params, + leaders) | 2,162 instances; subtitle/weld/elev marker in 607 views; `place_detail_component` → "not found" | new `place_annotation_symbol { viewId, symbols:[{ familyName, typeName \| typeId, location, rotationDegrees, parameters, leaders:[{end, elbow}] }] }` | `NewFamilyInstance(pt, sym, view)` + `AnnotationSymbol.addLeader()` ✅ |
| **G3** | Dimension text control | 1,916 overrides (`EQ`, `D/3`, `"b"`, `SEE PLAN`), 1,103 prefix/suffix/below (`TYP`, `MIN`, `CLR`) | `create_dimensions` per-dim `text:{ override, prefix, suffix, above, below }` and `segments:[{ index, override, … }]`; `modify_annotations setDimensionText` | `ValueOverride`, `Suffix`, `Below`, `Segments[i].ValueOverride` ✅ |
| **G3b** | Dimension type matched loosely | asked `Tick - 1/8" Arial`, got `… - Blank` (text invisible) | exact (case-insensitive) match first; only then contains; error listing candidates when ambiguous | pass `dimensionStyleId` |
| **G4** | Point-based dims in drafting views broken | pilot: value 0, single reference | in drafting/detail views, resolve points to the nearest **detail-curve end/edge reference** (or create invisible reference lines); add `references:[{ elementId, end:"start"/"end"/"curve" }]` for explicit control; multi-reference chains | dimension by detail-line `elementIds` ✅ |

### P1 — big productivity wins

| Id | Gap | Evidence | Proposed tool / change |
|---|---|---|---|
| **G5** | Batch results report `Success:true` when items fail ("Created 0 of 1") | pilot: hatch and subtitle silently not created | return `isError` (or `Success:false`) when `failed == total`; surface `failed` count in the MCP text summary for partial failures |
| **G6** | Detail groups | 2,442 instances / 307 types (screws, wire mesh, piles) | `place_detail_group { viewId, groupTypeName \| id, location }`, `create_detail_group { elementIds, name }`, list group types |
| **G7** | Rounded polylines (stirrups, ties, bent bars) | rebar drawn as lines in many views | `create_detail_lines` polyline `filletRadius` (per vertex or global) |
| **G8** | Mirror / flip detail items | 3,747 line-based items; break-line masking side depends on orientation | `modify_annotations mirror { axis }`, `flip`; doc the break-line side rule |
| **G9** | Arrays / spaced copies | rebar dots, nails, screws at o.c. spacing; 16 Array elements | `array_annotations { elementIds, count \| spacing, along:{start,end}, fitMode }` (plain copies, not Revit arrays) |
| **G10** | Detail reference integrity | 974 manual bubbles: 298 resolve, 535 placeholders, **141 point to nothing** | `audit_detail_references` (read-only, structured) + `sync_detail_references` (rewrite numbers from target view) + `create_view_reference` for live refs |
| **G11** | Copy details between views / documents | library → project is the main use of a typical-detail file | `copy_view_contents { sourceViewId, targetViewId, elementIds?, offset }`; `import_drafting_views { sourceDocumentPath, viewNames }` (Insert Views from File equivalent) |
| **G15** | Sheet layout helpers | 1–23 viewports per sheet; `list_sheets` (unfiltered) timed out at 30 s | `layout_detail_sheet { sheetId, viewIds, grid:{cols,rows}, order }` packing by viewport outline; viewport label offset/length; make `list_sheets` paginate / raise timeout |

### P2 — polish

| Id | Gap | Proposed |
|---|---|---|
| G12 | Drafted schedules (lines + text tables) | `create_drafted_table { origin, columns, rows, cells, textType, rulingStyles }` |
| G13 | Formatted text (numbered lists, bold/underline headings in NOTES) | `create_text_note` `format:{ list:"numbered", bold:[ranges], underline }` via `FormattedText` |
| G14 | Imperial input | accept `"2'-9\""`, `"3/4\""`, or `units:"in"` on drafting tools (library is 100% imperial; every call needs ×25.4) |
| G16 | `send_code_to_revit` hides runtime errors ("target of an invocation") | unwrap `TargetInvocationException.InnerException` message + stack line |
| G19 | Invisible boundary for filled/masking regions | accept `lineStyle:"Invisible"` → `<Invisible lines>` built-in style id |
| G20 | `capture_view` min `pixelSize` 256 and switching views as side effect | document; optional `restoreActiveView:true` |

### P3 — later

| Id | Gap | Note |
|---|---|---|
| G17 | CAD cleanup (64 imports in 19 views; 494 regions on 28 `*.dwg-N` fill types) | API cannot explode; offer "trace import to native lines" + purge unused CAD fill types |
| G18 | Library search by content ("find the detail with an HSS to WF shear tab") | index of view name + note text + families per view, served from SQLite (`data` catalog) |

## Suggested build order

1. **G5, G3b, G16** — small fixes in existing handlers that make every later
   step trustworthy (one PR).
2. **G1 + G3 + G4** — extend `create_text_note`, `create_dimensions`,
   `modify_annotations` (annotate catalog, no new tools).
3. **G2** — `place_annotation_symbol` (new tool, annotate catalog).
4. **G6, G7, G8, G9** — drafting productivity (groups, fillets, mirror, arrays).
5. **G10, G11, G15** — library ↔ project workflows (references, copy/import, sheet packing).

After each step, remove the matching workaround from the skills and replace it
with the new tool call.

## Findings from the vision rebuild of S2-112 (2026-10-03)

Sheet S2-112 (8 views) was printed to PNG and redrawn from the image only, using the new tools; result is sheet
S2-112V. Every view built with 0 failed items (regions, lines, components, symbols, leadered notes, dimensions).
New or re-prioritised gaps:

| Id | Gap | Evidence | Proposed |
|---|---|---|---|
| **G21** | Dimension text position / leader | `0" TYP`, `TIES @ 4" OC` are dragged off the dim line in the original | `text.position:{x,y}` (+ `leader`) on create_dimensions / setDimensionText (`Dimension.TextPosition`, `HasLeader`) |
| **G12** ↑P1 | Drafted tables | 2 of 8 views on the sheet are tables (77 text notes + 31 rule lines built by hand) | `create_drafted_table` (rows/cols/merged header cells, ruling styles, cell text alignment) |
| **G22** | Viewport title placement and contents | rebuilt titles sit higher; label offset not settable; original second title line (e.g. `FN-GB-01-WR`) comes from an unknown view parameter | `set_viewport_title { labelOffset, labelLineLength }` and document which view parameter feeds the title family |
| **G23** | Place a viewport by view point, not box centre | drafting viewports expose no transforms (`GetProjectionToSheetTransform` throws) | `place_viewport { anchor: { viewPoint, sheetPoint } }` using box centre − view outline centre |
| **G24** | Detail number swaps | setting numbers already used on the sheet fails; needed a temp-number pass | `update_viewports { detailNumber }` that resolves collisions internally |
| **G25** | Text width semantics | widths are paper mm; too-narrow widths silently re-wrap centred table text | return measured text extents; option `fitWidth: true` |

Operational issues hit (not tools): the plugin reads `commandRegistry.json` only at Revit startup, and the registry
had 5 commands (incl. `delete_element`) pointing at `SampleCommandSet.dll` — fixed by hand; worth a plugin check
that warns when a registry entry's assembly does not contain the command.

## Status after the tool build (2026-10-04)

Built and merged on `feat/drafting-gaps` (untested live until Revit restarts): `draw_detail`,
`create_drafted_table` (G12), `update_viewports` (G22–G24), `place_viewport` anchor/detailNumber (G23),
`list_drafting_types`, `create_text_note` `textNoteTypeName`, dimension text `position`/`leader` (G21),
`crop_image_grid`, `overlay_images`, `image_info`. The Revit MCP server is registered in Claude Code (`revit`).

Still script-based (read-only `send_code_to_revit`): `harvest-standards.cs` (revit-detail-standards) and
`audit-details.cs` (revit-detail-qa) — candidates for `get_drafting_standards` and `audit_detail_library` tools.
Still open: G14 imperial strings, G17 CAD cleanup, G18 library search, G25 text extents.
