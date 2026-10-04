# Structural modelling gaps — P181 TTTM Chu Lai test (2026-10-04)

Test: model the primary structure of P181 (THACO office/commercial building, 148 × 93.45 m, basement + 3 floors +
2 technical floors + roof; steel columns/beams with composite slabs, RC basement on piles) from the 94-sheet
structural PDF, using the Revit MCP tools. Every place a tool was missing, the fallback (`send_code_to_revit` or a
misused tool) is recorded here.

## Log

| Step | What was needed | Tool used | Result | Gap |
|---|---|---|---|---|
| 0 | Create/open the target project | none | user opened a new project manually | **S1 `open_or_create_project`** (template, path, save) + `get_document_info` (which doc is active) |
| 1 | Rename/move template levels L1/L2 | `modify_annotations setParameters` (works on any element) | OK | **S2 `modify_levels`** (rename, elevation, story flag) — today only via a drafting tool by accident |
| 1 | 5 new levels | `create_level` | OK (creates *architectural* floor plans) | S3: option to create **structural plans** instead of floor/ceiling plans |
| 2 | 29 grids, irregular spacing, labels C.5/E.8/F.6/G.5/H.5 | `send_code_to_revit` | OK | **S4 `create_grids` with explicit coordinates + labels** (`create_grid` only does uniform spacing, sequential labels, and always both directions) |
| 3 | Load WWF column/beam, concrete beam, pile cap families | `send_code_to_revit` (`Document.LoadFamily`) | OK | **S5 `load_family`** (path or library search) |
| 3 | Types C1/C2/C3 (welded I 1000×1000×20×20 …), CC1/CC2 concrete | `send_code_to_revit` (duplicate + set d/bf/tf/tw, b/h) | OK | **S6 `create_family_type`** (duplicate + set type params) — blocks almost every structural job |
| 3 | C1 cruciform section | approximated as WWF 1000×1000×40×40 | approximation | S7 custom/compound steel sections (cruciform, built-up) — profile family or Steel Fabrication |
| 4 | 324 columns over 6 storeys, base/top levels, rotation 0/90/45/315 | `create_point_based_element` | OK, top level inferred from height | S8 dedicated `create_structural_columns` (base/top **level names**, offsets, slanted, rotation, mark); the current tool is described as door/window/furniture and takes elevations, not levels |
| 4 | Payloads of 70–100 items pasted per call (≈13 KB each) | — | works but expensive | S9 bulk input from a file (CSV/JSON path) for any create tool |
| 4 | Read geometry from the drawings | PDF vector extraction outside Revit | OK | S10 `link_cad` / `import_cad` + `get_cad_geometry` (the set has DWGs; no tool links them) |
| 5 | 10 welded-I beam types (I-400x200x6x8 … I-1200x800x25x30) | `send_code_to_revit` | OK; box girders "I hộp" approximated as I | S6 again; S11 box/hollow built-up sections |
| 5 | 1,830 beams on 5 floors | `create_line_based_element` verified on 3 beams (level, top-justified, OK), bulk via `send_code_to_revit` from JSON | 1,817 kept | S9 bulk; S12 beam options: z-offset / start-end offsets, cross-section rotation, **slab-step zones** (−50/−100/−800/−1100/−1200 not modelled); `thickness`/`height` args are meaningless for beams |
| 5 | Roof framing (trusses, purlins) | — | not modelled | S13 trusses (`Structural Trusses` family + layout), purlin/girt arrays |
| 6 | 5 composite slabs t150 + basement slab | `create_surface_based_element` | created, **but wrong** (see B1) | S14 floor tool: inner loops/openings, slope/sub-elements, slab edge, per-zone offsets, structural flag, span direction, metal deck |
| 7 | Basement retaining walls 400 | `create_line_based_element` (OST_Walls) | created, **but wrong** (see B2) | S15 wall tool: type by name/id honoured, structural usage, top level constraint, location line |
| 8 | 163 pile caps (24 sizes from the cap schedule) | types via `send_code_to_revit`, placement test with `create_point_based_element` (wrong z, B3), bulk via `send_code_to_revit` | OK, hexagonal/triangular caps approximated as rectangles | S16 foundations: isolated footing/pile-cap placement by level + top offset, polygonal caps (foundation slab with boundary), wall footings |
| 9 | 685 piles D400/D500, L = 10.5 m | `send_code_to_revit` (round concrete columns as piles) | OK (stand-in family) | S17 deep foundations: pile family + `place_piles` (under cap, length, cut-off level, pile type), pile schedule from survey coordinates |
| 10 | 12 grade beams (concrete) | types via `send_code_to_revit`, placement with `create_line_based_element` | OK; 33 dashed GBs not extracted | S6 (concrete beam types) |
| — | Schedule tables drawn as graphics in the PDF (cap schedule) | read by vision (crop + transcribe) | OK | vision workflow worked (`crop_image_grid` equivalent) |

## Bugs found in existing tools (silent wrong results)

| Id | Tool | Symptom |
|---|---|---|
| **B1** | `create_surface_based_element` | `thickness` ignored for the type: all six slabs got "Concrete 100mm"; floors created **non-structural**. Reported success. |
| **B2** | `create_line_based_element` (walls) | `thickness` 400 gave "M_Exterior - Brick on Mtl. Stud"; wall non-structural; **top unconnected** (height only). Reported success. |
| **B3** | `create_point_based_element` | z semantics differ by category: columns treat `locationPoint.z` as absolute, footings use it as the offset from the level → cap placed 5.5 m too low. |
| B4 | `create_level` | always creates architectural floor + ceiling plans; no structural plans. |

## Result

Model in the user's new project: 7 levels, 29 grids, 324 steel/concrete columns (6 storeys), 1,817 steel beams + 12 grade
beams, 6 slabs, 4 basement walls, 163 pile caps, 685 piles. Not modelled: roof trusses/purlins, slab openings and steps,
bracing, stairs/ramps/tanks/pits, connections, rebar.

## Proposed structural tool set (priority order)

1. **S6 `create_family_type`** — duplicate a family/system type and set type parameters (d/bf/tw/tf, b/h, footing W/L/T,
   floor/wall core thickness). Unblocks columns, beams, footings, piles, floors, walls.
2. **S5 `load_family`** — by path or library search (`Structural Columns/Steel/M_WWF…`).
3. **Fix B1–B3** and give the create tools explicit `typeId`/`typeName`, `structural`, level names, top constraints.
4. **S4 `create_grids`** with explicit coordinates and labels (+ arcs); **S2 `modify_levels`**; S3 structural plans.
5. **S8 `create_structural_columns`, `create_beams`** with level names, offsets, justification, rotation, and **S9 bulk
   input from a CSV/JSON file** (1,800 beams cannot be pasted as tool arguments).
6. **S14 floors with openings/zones**, **S15 structural walls**, **S16 foundations**, **S17 piles**.
7. **S1 open/create/save project**, `get_document_info`; **S10 link CAD + read CAD geometry**.
8. S11/S7 custom sections (box, cruciform), S13 trusses, bracing, openings in beams.

## First build — status (2026-10-04, commit 49f454c on feat/drafting-gaps)

Live re-test in the P181 model (all test elements/types removed afterwards, model counts unchanged):

| Item | Result |
|---|---|
| S5 `load_family` | ✅ by name from the library (`M_Pile Cap-4 Pile`), already-loaded families returned with their types, unknown name fails cleanly; `searchOnly` lists paths |
| S6 `create_family_type` | ✅ WWF I600x300x10x16 (d/bf/tw/tf in mm), pile cap 2500×2500×1500, floor type 250 by `thickness`; re-run is idempotent (`created:false`); unknown parameter → warning + the type's parameter list (the type is still created — consider failing instead) |
| B1 floors | ✅ no typeId + thickness 175 → `Generic - 175mm`, 175 mm, structural |
| B2 walls | ✅ thickness 350 → `Generic - 350mm`, structural, top attached to TANG 1 |
| B3 footings | ✅ top at −5800 (HAM −5500 + offset −300); was −11300. Column placement unchanged (base TANG 1 → top TANG 2) |
| B5 (new) | ~~`analyze_model_statistics.projectName` returns the active view's name~~ — false alarm, see below |

Note: after `/mcp` reconnect the `revit` tools were not re-exposed to the running Claude Code session; the re-test used the
same MCP server through a one-call stdio client.

## Second batch — status (2026-10-04, merges 7f7d7f1, 564f9ad, 6473ec7 on feat/drafting-gaps)

Live re-test in the P181 model, in a test zone east of the building (x ≈ 200–230 m). All 830 test elements and the 7 test
grids were removed afterwards; model counts unchanged (324 columns + 685 piles, 1,829 beams, 6 floors, 4 walls, 163 caps,
29 grids).

| Item | Result |
|---|---|
| S1 `get_document_info` | ✅ title, path, active view, units |
| S4 `create_grids` | ✅ 6 lines with explicit labels (incl. `TA.5`) + 1 arc; an existing label ("1") is skipped, not duplicated |
| S2/S3 `modify_levels` | ✅ created structural plans for HAM and TANG 3; none for levels that already had one |
| S8/S9 `create_structural_columns` | ✅ 74/74 from a CSV `dataFile` (level names, offsets, rotation, mark) |
| S8/S9 `create_beams` | ✅ 738/738 from a JSON file in 6.7 s; z-offset −800 gives top of steel −850 (no double offset) |
| S14 `create_slabs` | ✅ openings (array of polygons), stepped zone −850, foundation slab 300; types auto-created by thickness |
| S16 `create_foundations` | ✅ isolated cap at top −5800; hexagonal cap as a foundation slab 1500 |
| S17 piles | ✅ 6 piles placed under the caps by `underFoundationMark`, top at −7300 |
| B5 | not a bug: the user had saved the file as "P181 STR 3D (MCP check).rvt", which is also the name of the view |
| **S9b (new)** | bulk tools return every per-item result (738 rows); they should return a summary (counts, failures, id range) by default, with per-item detail on request |

Still open: S13 trusses/purlins, S10 CAD link + geometry, S7/S11 custom sections, the client's 2-minute timeout per chunk,
and `create_family_type` should fail (not warn) on unknown parameters.

## B6 — pile caps drop when piles are structural columns (2026-10-04)

The user spotted it in 3D. 156 of the 163 caps in the MCP model had their tops at −17,800 (the pile bottoms), not −5,800.
Revit attaches an isolated footing to the base of any structural column inside its footprint, and it enforces this when
the transaction commits. It doesn't matter whether the pile or the cap is created first, whether the pile is point- or
curve-driven, or whether it's moved in later; resetting the cap offset gets reverted at commit too. `create_foundations`
made it worse: it reported `topElevationMm −5800`, which is computed before the commit.

Fix: `create_foundations` now refuses a column-family pile when an isolated footing sits over it, and points the user to a
Structural Foundations pile family. It also reads the pile length from `Depth`. The reference model uses `M_Pile-Spun pile`
(foundation category; `Depth` type parameter, `Minimum Embedment` 150).

Repaired and re-tested live (2026-10-04): the guard refuses a column pile under cap 1F4. `M_Pile-Spun pile` was copied from the reference model (opened detached in the background, closed unsaved), with types `PLIE D500/D400 L10500` (Depth 10500). The 685 column piles were replaced via `create_foundations dataFile` (685/685, marks `<cap>-<n>`), and the caps were reset. After commit: all 163 caps have their tops at −5800, and the piles run from −7150 (150 embedment) to −17800 (−17300 under 1000 caps). Note: the reference model has its cap tops at −5500 (level), not −5800 as read from the PDF.

## Lessons from the hand-built reference model (P181-TTTM_CHULAI-R19)

The conventions are captured in the skill `.claude/skills/revit-structural-model`. Gaps they expose:

| Id | Need | Reference model |
|---|---|---|
| S18 | `create_beam_systems`: boundary or by picking supporting beams, beam type, layout rule (fixed number / max spacing / fixed distance), 3D, direction | 479 beam systems hold 1,749 joists and purlins |
| S19 | Workset per created element (`workset` arg on every create tool, or `set_workset`) | 28 worksets: S-COLUMN, S-BEAM, S-PILE, S-TEST PILE … |
| S20 | Beam options: structural usage (girder/joist/purlin), start/end level offsets instead of z-offset | All beams are Top-justified with level offsets; usage is set |
| S21 | Column top/base attachment to beams, roofs and trusses | 128 columns are top-attached |
| S22 | Pile marks/comments derived from the cap (`<cap>-<n>`), test-pile flag | Pile Mark `1F43-7`, Comments `1F43`, `Test Pile` yes/no |
| S13 | Trusses (`Truss_12Panel`, 30 m spans) | 16 trusses with 25 members each |

## S23 `copy_families` + `load_family` folder/types (2026-10-04)

The B6 repair needed a script to take `M_Pile-Spun pile` from the reference model. New and extended tools:

- `copy_families`: the source is an open project (`sourceDocument`) or a file (`sourcePath`; opened in the background
  detached with worksets closed, and closed unsaved).
  - `listOnly` lists the source's families and system types.
  - `families` (names or wildcards) or `categories` choose what to copy; families come via the family editor with all their types.
  - `systemTypes` copies floor, wall and similar types via Copy/Paste, keeping existing names.
  - `overwrite`; `saveToFolder` also writes the families as .rfa files.
- `load_family`: `folder` (+ `pattern`, `recursive`) loads every .rfa in a folder, skipping backup files; `types` loads only selected types.
- The client timeout is now per command: `copy_families` and `load_family` wait up to 10 minutes, all other commands still 2 minutes.

## The Nexus Tower 3 test — RC tower from vector PDFs (2026-10-04)

The test modelled the primary structure of THE NEXUS Tower 3 into a blank project. The structure is 5 basements plus 37 floors plus a roof: a PT flat slab with band beams, a shear-wall core, a diaphragm wall, and barrette piles. The source was two vector PDFs, SUB (92 sheets) and SUP (119 sheets).

The geometry was read from the PDF vectors with PyMuPDF and OpenCV. The scripts live outside the repo in `E:\VNguyen\RnD\revit\ai-modeling\nexus_work\py`, and each one handles a different part of the drawings:

- **Grids:** taken from the bubbles.
- **Walls and columns:** read from the grey cut fill, raster-decomposed into wall centrelines and rotated columns.
- **Beams:** found from their labels plus a parallel edge pair at the beam width, with the size from the label or the on-sheet schedule.
- **Slab outlines:** a flood fill of the 0.6 pt edge lines.
- **Shaft openings:** detected from the dashed "X" diagonals.
- **Barrettes:** found from parallel 2800 mm edges.

Result: 43 levels, 19 grids, 524 columns, 1278 walls, 1641 beams, 43 slabs, 1 core raft cap, 11 caps and 30 barrettes. No create call failed.

| Step | What was needed | Tool used | Result | Gap |
|---|---|---|---|---|
| Walls | 1278 RC walls per storey, base/top level by name, 14 thicknesses | `send_code_to_revit` reading a CSV (Wall.Create, height type = top level) | ok | **S24 `create_walls`**: bulk walls from a dataFile with baseLevel/topLevel names, offsets, thickness → type, structural, location line. `create_line_based_element` only takes numeric elevations and has no dataFile |
| Beam types | 71 `B-WxH` concrete types | `send_code_to_revit` (duplicate + b/h) | ok | `create_family_type` works but has no dataFile; 71 items had to be pasted. **S25** dataFile on `create_family_type` |
| Barrettes | Rectangular 1200/1500×2800 piles, 70/78 m | `M_Footing-Rectangular` types with `Foundation Thickness` = pile length, placed as `isolated`, topOffset −3000 | ok, geometry right | **S26** rectangular barrette pile family / `kind:'pile'` with section B×L and rotation |
| Large results | create_* over 500 items returned 70–200 kB JSON, more than the client limit | — | read back from the saved file | **S27** `summary:true` on bulk create tools (counts + failures only) |
| Core walls | Wall pieces overlap at L/C corners (centrelines extended to the other wall's axis) | — | acceptable, Revit joins | — |

### Tools added for the Nexus gaps (branch feat/modeling-gaps-2, not yet tested in live Revit)

| Gap | Tool | Notes |
|---|---|---|
| Reading drawings | `pdf_extract` (server-side, read-only) | Grid calibration from bubbles, text, cut fills → columns and wall centrelines, beams from labels and schedules, slab outline and X openings, rectangles (barrettes) |
| S24 walls | `create_walls` | Bulk with dataFile, base/top level by name, offsets, thickness → type, location line, independent bottom (diaphragm walls) |
| S25 types | `create_family_type` + dataFile | Through the shared bulk runner |
| S27 large results | `summary` on every bulk tool, auto-summary over 60 kB | Totals, failures, warning counts, id ranges |
| Typical floors | `copy_to_levels` | Paste Aligned to Selected Levels with re-hosting (level and offsets kept) |
| Re-runs | `delete_elements` | Filter by category, level, comments, mark or type; dryRun by default |
| S19 worksets | `set_workset` | List, create, move filtered elements, enable worksharing |
| Shafts and openings | `create_openings` | Shafts between levels, wall openings, floor openings |
| PT zones and drop panels | `create_slabs` `zones` / `dropPanels` | Zones cut from the main slab with their own thickness/offset; drop panels hang under the soffit |
| Stairs | `create_stairs` | Straight or U-shaped component stairs |
| Joins | `join_elements` | Bulk join with cut order (columns cut beams/slabs, walls cut beams …) |
| QA | `check_model` | Counts, elevations per type, overlaps/clashes, unsupported beams/columns, levels without floor |
| S1 | `save_document`, `open_document` | Save/Save As; open, detach, or new project from a template |
| S26 barrettes | `create_family` | See below |

**S26: parametric families when none exists (`create_family`).** It builds an extruded family from `Metric Structural Foundation.rft` (or Generic Model):

- The plan profile is rectangular (Width × Length) or circular (Diameter).
- The side faces are locked to reference planes, which are driven by labelled dimensions and EQ constraints about the centre planes.
- `Depth` drives the extrusion end; it is a type parameter, or an instance parameter for piles whose length varies.
- The insertion point is the pile top, and the extrusion runs down from it.
- A flex test runs before saving.
- The `.rfa` is saved to a library folder and loaded, and the types are created.
- `create_foundations kind:'pile'` now accepts `rotationDeg` and `lengthParameter`. It looks for `Depth` before `Length`, so a barrette's plan Length is never taken as the pile length.

The research route was the Family API: `NewFamilyDocument` → `FamilyManager.AddParameter` → `NewReferencePlane` / `NewExtrusion` → `NewAlignment` locks → labelled `NewDimension` / `NewDiameterDimension` → `AssociateElementParameterToFamilyParameter(EXTRUSION_END_PARAM)`. The rejected alternatives:

- **DirectShape:** cannot be scheduled as typed families.
- **Abusing `M_Footing-Rectangular`:** only works for rectangles, and `Foundation Thickness` = pile length is misleading in schedules.

The first live prototype run crashed Revit, so the tool must be verified step by step before relying on it.

### Live retest in Revit 2025 (2026-10-04, model `Nexus_T3_MCP.rvt`)

The tower was rebuilt with the new tools. It has 43 levels, 524 columns, 1278 walls, 1641 beams, 43 slabs, 12 caps and 30 barrettes, and it was saved through `save_document`. The typical floors L6–18 and L24–35 came from `copy_to_levels` (847 elements), and `check_model` confirmed 1641 beams with every copy at its level.

| Tool | Result |
|---|---|
| save_document, create_walls, create_family_type (dataFile), bulk `summary`, create_openings (shaft), create_stairs (U), check_model, set_workset (list) | Passed first time |
| copy_to_levels, delete_elements, set_workset | **Bug fixed:** `Enum.IsDefined(typeof(BuiltInCategory), (int)value)` throws in 2024+ (the enum is Int64); cast to the enum instead |
| create_family (circular) | **Bug fixed:** the diameter dimension on the solid edge did not drive; the dimension now references the sketch arc (flex 1500 ✓) |
| create_family (material) | **Bug fixed:** associating Structural Material failed until its value was a real material; it is now set to Concrete first, inside a sub-transaction |
| join_elements | **Extended:** `cutType`/`byType` filters enforce the cut order for same-category pairs. Revit auto-joins the raft and the core cap with the raft cutting, which left the cap top at −18500. Full tower: 9506 pairs tested, 1285 joined, 1000 switched, 0 failed, 28 s |
| create_foundations kind:'pile' | `MCP_Pile-Barrette` with rotationDeg and a cap underside. Length comes from `Depth`, so the plan `Length` is never mistaken for the pile length |
| plugin | The ribbon switch was disabled on the start page; an `AlwaysAvailable` availability class is added (deploy pending) |

Known minor issues:

- **copy_to_levels:** `extraCopies` is reported for beams whose geometry Revit trimmed; the copies themselves are correct.
- **Openings:** cannot take a Mark.
- **Summary ids:** ranges do not compress ids that step by 2.
- **check_model:** reported 18 duplicate-like wall overlaps and 213 beam ends short of supports. These are errors in the extraction data, now easy to find.

## Drawing production test — sheets like the PDF (2026-10-04)

The test produced three A1 sheets at 1:150 in `Nexus_T3_MCP.rvt`: S-BB-100100 PILE CAP LAYOUT, S-T3-100201 COLUMN & SHEAR WALLS LAYOUT L1–L2, and S-T3-100104 FLOOR FRAMING PLAN L5–18 with a beam schedule. It used create_view, set_view_range, set_crop_region, override_graphics, create_view_filter, create_grid_dimensions, tag_elements, create_schedule, create_sheet, place_viewport and create_text_note. Everything below either needed `send_code_to_revit` or is still missing.

| # | Gap | What happened | Proposed tool / fix |
|---|---|---|---|
| D1 | **Grid vertical extents (bug)** | `create_grids` leaves Revit's default ±4572 mm, so grids were invisible on 40 of 43 levels | create_grids: span all levels by default (`verticalExtent: allLevels`) — fix now |
| D2 | **Grid display per view** | Bubble ends and 2D extents need per-view settings; End0/End1 is not predictable, and bubbles collided (B2/B, D2/E1) | `set_grid_display {viewIds, bubbles: top/bottom/left/right/both/none per grid or group, clipToCrop, offset2D}` |
| D3 | **Template view filters hid the model** | The template's "Walls/Floors - Concrete NOT" filters hid every MCP-made wall and slab (types named `RC Wall`/`Slab`) | `set_view_filters {viewIds, filter, visible/remove}`; also name generated types with the material ("Concrete") or set the Structural Material |
| D4 | **Structural plan graphics** | Beams were drawn solid where the PDF draws them dashed under the slab; no cut fill or slab edges by default | `apply_drawing_style {kind: framingPlan|columnPlan|foundationPlan}` or build view templates via a `create_view_template` tool (hidden framing, cut fill, halftone below) |
| D5 | **Tag content** | Default tags show the type name (`B-2000x450`). The PDF uses Mark + size (`L5-18.HB6-2000x450`), column marks (`C4`), cap marks (`T3-F2`) and pile marks (`SGBR-01.1`) | `create_tag_family {category, label: [Mark, '-', b, 'x', h]}` from tag templates, or choose a tag type by label fields |
| D6 | **Tag placement** | Tags overlap in the core and over cap+pile stacks; there is no aligned or along-beam offset | tag_elements: `avoidOverlaps`, `alongElement`, per-category offsets; auto-arrange after `find_tag_overlaps` |
| D7 | **Wall marks** | The PDF has CW1–CW4 per core wall group; MCP walls have no marks and are pieces | `set_parameters` in bulk (mark by group), plus wall tags |
| D8 | **Element dimensions** | The PDF dimensions column and cap sizes and their offsets to grids (550/550, 2300/3600); create_dimensions works only on picked references | `dimension_elements {elementIds, toGrids: true, sizes: true, side}` (faces of columns, caps, walls to the nearest grids) |
| D9 | **Grid dimension grouping** | Grids 6/7 were chained with 2–5 and inclined grids were dimensioned at the "Start" end; explicit gridIds plus sides worked | create_grid_dimensions: group by side/extent automatically, follow bubble side |
| D10 | **Dimension and text sizes** | Dimension text was unreadable at 1:150 with the template types | `create_dimension_type / set_annotation_style {textSize, font, tick}`, or copy from the office template (copy_families systemTypes) |
| D11 | **Schedule filter on level** | A filter by `Reference Level = LEVEL 5` failed because the value is an ElementId; fixed with C# | create_schedule: resolve level/element-id filter values by name |
| D12 | **Thickness hatches / legend** | The PDF hatches caps and slabs by thickness with a legend; filters plus fill patterns are possible, but there is no legend view tool | `create_legend` (legend components + text) and filter-by-type helpers |
| D13 | **Title block** | Generic Autodesk A1; long sheet names overflow the title box | copy the office title block (copy_families) and `set_titleblock_params`; sheet name wrap rules |
| D14 | **Sections / callouts on plans** | The PDF has sections 1-1/2-2 on the column sheet; create_view can make sections but there is no "section from plan with tags on sheet" workflow | Combine create_view (section) + place_viewport (works) — needs a recipe/skill rather than a tool |
| D15 | **Slab thickness tags / spot elevations** | The PDF has PT-250 / S-300 labels and +138.250 spots; create_spot_elevations exists but there is no floor tag type with thickness | Floor tag family with thickness (D5) + spot elevations |
| D16 | **copy_to_levels report** | `extraCopies` is noisy when beams were trimmed by joins (the copies are correct) | Match copies by element id order or by bounding-box centre |
