using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Framing
{
    /// <summary>
    ///     Places structural columns at plan points between a base and a top
    ///     level, one sub-transaction per column. Without a top level the column
    ///     takes `height` (attached to the level at that height when one exists)
    ///     or runs to the next level up.
    /// </summary>
    public class CreateStructuralColumnsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Create Structural Columns";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "columns");
            var lookup = new FramingLookup(doc, BuiltInCategory.OST_StructuralColumns, "Structural Columns");
            var results = DocumentationUtils.RunBatch(doc, "MCP: Create Structural Columns", items,
                item => Create(doc, lookup, (JObject)item));
            return Ok($"Created {results.Count(r => r.Value<bool>("success"))} of {results.Count} columns.",
                DocumentationUtils.Summarize(results));
        }

        private static object Create(Document doc, FramingLookup lookup, JObject item)
        {
            var symbol = lookup.ResolveType(item);
            var x = FramingLookup.RequireMm(item, "x");
            var y = FramingLookup.RequireMm(item, "y");
            var baseLevel = lookup.ResolveLevel(item["baseLevel"], "baseLevel");
            var baseOffset = FramingLookup.ReadMm(item, "baseOffset") ?? 0;
            var topOffsetInput = FramingLookup.ReadMm(item, "topOffset");
            var height = FramingLookup.ReadMm(item, "height");
            var warnings = new List<string>();

            Level topLevel;
            double topOffset;
            var topToken = item["topLevel"];
            if (topToken != null && topToken.Type != JTokenType.Null)
            {
                topLevel = lookup.ResolveLevel(topToken, "topLevel");
                topOffset = topOffsetInput ?? 0;
                if (height.HasValue)
                    warnings.Add("'height' ignored because topLevel is given.");
            }
            else if (height.HasValue)
            {
                var topElevation = baseLevel.Elevation + baseOffset + height.Value;
                topLevel = lookup.LevelAt(topElevation);
                if (topLevel != null)
                {
                    topOffset = topOffsetInput ?? 0;
                }
                else
                {
                    // Unconnected height: Revit keeps the base level as top level with the height as top offset.
                    topLevel = baseLevel;
                    topOffset = baseOffset + height.Value;
                    warnings.Add($"No level at {DocumentationUtils.FeetToMm(topElevation)} mm; top set to '{baseLevel.Name}' + {DocumentationUtils.FeetToMm(topOffset)} mm (unconnected height).");
                    if (topOffsetInput.HasValue)
                        warnings.Add("'topOffset' ignored for an unconnected height.");
                }
            }
            else
            {
                topLevel = lookup.LevelAbove(baseLevel)
                           ?? throw new ArgumentException($"No level above '{baseLevel.Name}'; give topLevel or height.");
                topOffset = topOffsetInput ?? 0;
            }

            var bottom = baseLevel.Elevation + baseOffset;
            var top = topLevel.Elevation + topOffset;
            if (top - bottom < FramingLookup.LevelTolerance)
                throw new ArgumentException(
                    $"Column top ({DocumentationUtils.FeetToMm(top)} mm) must be above its base ({DocumentationUtils.FeetToMm(bottom)} mm).");

            if (!symbol.IsActive)
                symbol.Activate();
            var point = new XYZ(x, y, baseLevel.Elevation);
            var column = doc.Create.NewFamilyInstance(point, symbol, baseLevel, StructuralType.Column)
                         ?? throw new InvalidOperationException("Revit did not create the column.");

            // Order the writes so the top never sits below the base in between.
            void SetBase() => FramingLookup.SetParameter(column, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM, p => p.Set(baseOffset), "baseOffset", warnings);
            void SetTop()
            {
                FramingLookup.SetParameter(column, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, p => p.Set(topLevel.Id), "topLevel", warnings);
                FramingLookup.SetParameter(column, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, p => p.Set(topOffset), "topOffset", warnings);
            }

            if (baseOffset <= 0)
            {
                SetBase();
                SetTop();
            }
            else
            {
                SetTop();
                SetBase();
            }

            var rotation = item.Value<double?>("rotationDeg") ?? 0;
            if (Math.Abs(rotation) > 1e-9)
            {
                var axis = Line.CreateBound(point, point + XYZ.BasisZ);
                ElementTransformUtils.RotateElement(doc, column.Id, axis, rotation * Math.PI / 180);
            }

            FramingLookup.SetIdentity(column, item, warnings);

            var result = new JObject
            {
                ["id"] = column.Id.GetValue(),
                ["typeId"] = symbol.Id.GetValue(),
                ["baseLevel"] = baseLevel.Name,
                ["topLevel"] = topLevel.Name,
                ["baseOffset"] = DocumentationUtils.FeetToMm(baseOffset),
                ["topOffset"] = DocumentationUtils.FeetToMm(topOffset)
            };
            if (warnings.Count > 0)
                result["warnings"] = new JArray(warnings);
            return result;
        }
    }
}
