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
