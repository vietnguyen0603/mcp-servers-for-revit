# Project style profile (`revit-mcp.project-style/1`)

`export_project_style` writes a project's drawing standards to a JSON file so another agent can
re-create the style in a different project, either by copying from the source file directly
(`copy_project_standards`) or by rebuilding it item by item with `manage_graphics_standards`,
`manage_annotation_types` and `manage_view_templates`.

All values are already in tool units:

| Quantity      | Unit / form                                                         |
|---------------|---------------------------------------------------------------------|
| length        | millimetres (`...Mm`, `textSizeMm`); paper sizes for annotation     |
| angle         | degrees (`angleDeg`)                                                |
| colour        | `[r, g, b]`, 0-255                                                  |
| line weight   | pen number 1-16 (`weight`, `cutWeight`, `...LineWeight`)            |
| references    | by name: line/fill patterns, materials, arrowheads, `Family : Type` |

Missing keys mean "not set / not applicable / Revit default". Order inside each array is by name.

## Top level

```json
{
  "schema": "revit-mcp.project-style/1",
  "units": { "length": "mm", "angle": "deg", "color": "[r,g,b] 0-255", "lineWeight": "pen number 1-16" },
  "source": { "title": "Office.rte", "path": "C:/Standards/Office.rte", "revitVersion": "2025", "exportedAt": "2026-10-04T08:00:00Z" },
  "linePatterns": [], "fillPatterns": [], "lineStyles": [], "objectStyles": [],
  "arrowheads": [], "textTypes": [], "dimensionTypes": [], "gridTypes": [], "levelTypes": [], "viewportTypes": [],
  "viewFilters": [], "viewTemplates": [],
  "tagFamilies": [], "titleBlocks": [], "annotationSymbols": [], "otherAnnotationFamilies": [], "defaultTypes": [],
  "materials": [], "wallTypes": [], "floorTypes": []
}
```

`materials`, `wallTypes` and `floorTypes` are only present when asked for in `sections`.
Every type entry (arrowheads ... viewportTypes, wallTypes, floorTypes) and every view template also
has `parameters`: all writable parameters by display name in the units above (omit with
`includeParameters:false`). Use the named keys first; `parameters` covers everything else.

## Graphics standards (`manage_graphics_standards`)

```json
"linePatterns": [
  { "name": "Dash 3mm", "segments": [ { "type": "dash", "lengthMm": 3 }, { "type": "space", "lengthMm": 1.5 }, { "type": "dot", "lengthMm": 0 } ] }
],
"fillPatterns": [
  { "name": "Solid fill", "target": "drafting", "solid": true },
  { "name": "Diagonal 45 2mm", "target": "drafting", "solid": false, "hostOrientation": "ToView",
    "grids": [ { "angleDeg": 45, "originMm": [0, 0], "offsetMm": 2, "shiftMm": 0, "segmentsMm": [] } ] }
],
"lineStyles": [
  { "name": "S-DETL-THIN", "weight": 1, "color": [0, 0, 0], "pattern": "Solid" },
  { "name": "<Hidden>", "weight": 1, "color": [0, 0, 0], "pattern": "Hidden", "builtIn": true }
],
"objectStyles": [
  { "category": "Structural Framing", "builtInCategory": "OST_StructuralFraming", "type": "model",
    "weight": 2, "cutWeight": 5, "color": [0, 0, 0], "pattern": "Solid", "material": "Concrete",
    "subcategories": [ { "name": "Hidden Lines", "weight": 1, "color": [0, 0, 0], "pattern": "Hidden" } ] }
]
```

- `segments`: dash/space lengths in mm; a dot has length 0. `pattern: "Solid"` is the built-in solid line.
- Fill `grids`: `segmentsMm` empty = continuous lines; drafting patterns are paper mm, model patterns model mm.
- `objectStyles[].type` is `model` or `annotation`; `cutWeight` only exists for cuttable categories.

`materials` (optional): `{ name, class, color, transparency, surfaceForegroundPattern, surfaceForegroundColor,
surfaceBackgroundPattern/Color, cutForegroundPattern/Color, cutBackgroundPattern/Color }`.

## Annotation types (`manage_annotation_types`)

```json
"arrowheads":   [ { "name": "Diagonal 3mm", "style": "Diagonal", "tickSizeMm": 3, "filled": false, "parameters": {} } ],
"textTypes":    [ { "name": "2.5mm Arial", "font": "Arial", "textSizeMm": 2.5, "bold": false, "italic": false, "underline": false,
                    "widthFactor": 1, "color": [0,0,0], "weight": 1, "background": "transparent", "showBorder": false,
                    "leaderArrowhead": "Arrow Filled 15 Degree", "tabSizeMm": 12.7, "leaderBorderOffsetMm": 2, "parameters": {} } ],
"dimensionTypes": [ { "name": "Linear - 2.5mm Arial", "styleType": "Linear", "family": "Linear Dimension Style",
                    "font": "Arial", "textSizeMm": 2.5, "textOffsetMm": 1, "widthFactor": 1, "color": [0,0,0], "weight": 1,
                    "tickMark": "Diagonal 3mm", "tickMarkWeight": 3, "interiorTickMark": "Diagonal 3mm",
                    "witnessLineGapMm": 1.5, "witnessLineExtensionMm": 2.5, "dimLineSnapDistanceMm": 8,
                    "textBackground": "transparent",
                    "units": { "useProjectSettings": false, "unit": "autodesk.unit.unit:millimeters-1.0.1", "accuracy": 1 },
                    "parameters": {} } ],
"gridTypes":    [ { "name": "6.5mm Bubble", "symbol": "M_Grid Head - Circle : 6.5mm", "bubbleEnd1": true, "bubbleEnd2": false,
                    "centerSegment": "Continuous", "centerWeight": 1, "centerColor": [0,0,0], "centerPattern": "Grid Line",
                    "endWeight": 1, "endColor": [0,0,0], "endPattern": "Solid", "endSegmentLengthMm": 25, "parameters": {} } ],
"levelTypes":   [ { "name": "Level Head", "symbol": "M_Level Head - Circle : M_Level Head - Circle", "weight": 1, "color": [0,0,0], "pattern": "Centre", "parameters": {} } ],
"viewportTypes":[ { "name": "Title w Line", "title": "M_View Title : M_View Title", "showTitle": "Yes", "showExtensionLine": true,
                    "weight": 1, "color": [0,0,0], "pattern": "Solid", "parameters": {} } ]
```

- `dimensionTypes[].styleType` distinguishes Linear, Angular, Radial, Diameter, ArcLength, SpotElevation,
  SpotCoordinate, SpotSlope ... (Revit `DimensionStyleType`). `family` is the Revit system family name.
- `units.unit` is a ForgeTypeId string (Revit 2022+) or a `DisplayUnitType` name (2020-2021).
- `style`, `centerSegment` and `showTitle` are Revit's display strings for enumerated parameters.

## View filters and view templates (`manage_view_templates`)

```json
"viewFilters": [
  { "name": "S-Transfer Beams", "type": "rule", "categories": ["OST_StructuralFraming"], "logic": "And",
    "rules": [ { "parameter": "Comments", "builtInParameter": "ALL_MODEL_INSTANCE_COMMENTS", "operator": "Contains", "value": "TRANSFER" },
               { "parameter": "Depth", "operator": "Greater", "value": 900, "unit": "mm" } ],
    "groups": [ { "logic": "Or", "rules": [] } ] },
  { "name": "Selected walls", "type": "selection", "elementCount": 12 }
]
```

- `categories` are `OST_` names (display names for non built-in categories). Rule operators use the
  `create_view_filter` vocabulary: Equals, NotEquals, Greater, GreaterOrEqual, Less, LessOrEqual,
  Contains, NotContains, BeginsWith, NotBeginsWith, EndsWith, NotEndsWith, HasValue, HasNoValue
  (plus `ParameterExists`). Length values are mm when `unit: "mm"` is present. Element-id rules give
  the id in `value` and the referenced name in `valueName`. `groups` holds nested And/Or sets.
- Selection filters reference elements and cannot be re-created in another project.

```json
"viewTemplates": [
  { "name": "S-Framing Plan 1:100", "viewType": "FloorPlan", "scale": 100, "detailLevel": "Medium",
    "discipline": "Structural", "displayStyle": "HLR", "partsVisibility": "ShowOriginal", "phaseFilter": "Show Complete",
    "viewRange": { "top": { "level": "associatedLevel", "offsetMm": 2300 }, "cut": { "level": "associatedLevel", "offsetMm": 1200 },
                   "bottom": { "level": "associatedLevel", "offsetMm": 0 }, "viewDepth": { "level": "levelBelow", "offsetMm": 0 } },
    "controlledParameters": ["Detail Level", "V/G Overrides Model", "View Range", "View Scale"],
    "notControlledParameters": ["Phase"],
    "categoryOverrides": [
      { "category": "Furniture", "builtInCategory": "OST_Furniture", "visible": false },
      { "category": "Structural Framing: Hidden Lines", "projectionLinePattern": "Hidden", "projectionLineWeight": 1 },
      { "category": "Floors", "builtInCategory": "OST_Floors", "halftone": true, "cutLineWeight": 3,
        "surfaceForegroundPattern": "Solid fill", "surfaceForegroundColor": [192, 192, 192] }
    ],
    "filters": [ { "name": "S-Transfer Beams", "visible": true, "enabled": true,
                   "overrides": { "projectionLineColor": [255, 0, 0], "projectionLineWeight": 5 } } ],
    "parameters": { "View Scale": 100, "Detail Level": 2 } }
]
```

- `viewRange` exists for plan templates. `level` is `associatedLevel`, `levelAbove`, `levelBelow`,
  `unlimited` or a level name; `offsetMm` is from that level.
- `categoryOverrides` lists only categories that are hidden (`visible: false`) or overridden;
  subcategories are named `"Parent: Sub"`. Override keys mirror `override_graphics`:
  `halftone`, `transparency` (0-100), `projectionLineColor/Weight/Pattern`, `cutLineColor/Weight/Pattern`,
  `surfaceForegroundPattern/Color`, `surfaceBackgroundPattern/Color`, `cutForegroundPattern/Color`,
  `cutBackgroundPattern/Color` (+ `...Visible: false` when a pattern is switched off), `detailLevel`.
- `filters` are in the template's order (Revit 2022+; earlier versions give no order and no `enabled`).
- `controlledParameters` are the "Include" ticks of the template; `notControlledParameters` are unticked.

## Families and defaults

```json
"tagFamilies": [
  { "category": "Structural Framing Tags", "builtInCategory": "OST_StructuralFramingTags", "defaultType": "M_Structural Framing Tag : Standard",
    "families": [ { "name": "M_Structural Framing Tag", "types": ["Boxed", "Standard"] } ] }
],
"titleBlocks":       [ { "name": "A1 Office", "types": ["A1"] } ],
"annotationSymbols": [ { "name": "North Arrow", "types": ["North Arrow"] } ],
"otherAnnotationFamilies": [ { "category": "Section Marks", "builtInCategory": "OST_SectionHeads", "families": [] } ],
"defaultTypes": [ { "group": "TextNoteType", "family": "Text", "type": "2.5mm Arial" },
                  { "group": "LinearDimensionType", "family": "Linear Dimension Style", "type": "Linear - 2.5mm Arial" } ]
```

Families cannot be rebuilt from JSON; copy them with `copy_project_standards` (`tagFamilies`,
`titleBlocks`, `annotationSymbols`) or `copy_families`, or load them with `load_family`.
`defaultTypes[].group` is a Revit `ElementTypeGroup` name.

## Re-applying a profile

1. If the source file is reachable, prefer `copy_project_standards` with the source path - it copies
   exact elements (including families and template overrides) in one call.
2. Otherwise, re-create in dependency order: line patterns, fill patterns, line styles, object styles,
   arrowheads, text/dimension/grid/level/viewport types, view filters, view templates, then set the
   default types.
