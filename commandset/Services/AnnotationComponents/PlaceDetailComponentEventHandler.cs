using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Places detail component families (OST_DetailComponents) in a view.
    ///     Point-based families use <c>location</c> and optional rotation;
    ///     line-based families use <c>start</c>/<c>end</c>.
    /// </summary>
    public class PlaceDetailComponentEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Place Detail Component";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var view = DetailGeometry.ResolveView(uiDoc, parameters);
            var items = DocumentationUtils.RequireArray(parameters, "components");
            var symbols = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_DetailComponents)
                .WhereElementIsElementType()
                .OfType<FamilySymbol>()
                .OrderBy(s => s.FamilyName)
                .ThenBy(s => s.Name)
                .ToList();

            var results = DocumentationUtils.RunBatch(doc, "MCP: Place Detail Components", items, token =>
            {
                var item = (JObject)token;
                var symbol = ResolveSymbol(symbols, item);
                if (!symbol.IsActive)
                {
                    symbol.Activate();
                    doc.Regenerate();
                }

                FamilyInstance instance;
                var placement = symbol.Family.FamilyPlacementType;
                if (placement == FamilyPlacementType.CurveBasedDetail)
                {
                    var start = DetailGeometry.ReadPoint(view, item["start"], "start");
                    var end = DetailGeometry.ReadPoint(view, item["end"], "end");
                    instance = doc.Create.NewFamilyInstance(Line.CreateBound(start, end), symbol, view);
                }
                else if (placement == FamilyPlacementType.ViewBased)
                {
                    var location = DetailGeometry.ReadPoint(view, item["location"], "location");
                    instance = doc.Create.NewFamilyInstance(location, symbol, view);
                    var rotation = item.Value<double?>("rotationDegrees") ?? 0;
                    if (Math.Abs(rotation) > 1e-9)
                    {
                        var axis = Line.CreateBound(location, location + view.ViewDirection);
                        ElementTransformUtils.RotateElement(doc, instance.Id, axis, rotation * Math.PI / 180);
                    }
                }
                else
                {
                    throw new ArgumentException(
                        $"'{symbol.FamilyName}: {symbol.Name}' has placement type {placement}; only view-based and line-based detail items are supported.");
                }

                var warnings = new List<string>();
                if (item["parameters"] is JObject values)
                {
                    foreach (var property in values.Properties())
                    {
                        var error = DocumentationUtils.SetParameterValue(instance, property.Name, property.Value);
                        if (error != null && instance.LookupParameter(property.Name) == null
                                          && instance.Symbol.LookupParameter(property.Name) != null)
                            error = $"'{property.Name}' is a type parameter; it was not changed.";
                        if (error != null)
                            warnings.Add(error);
                    }
                }

                return new
                {
                    instanceId = instance.Id.GetValue(),
                    familyName = symbol.FamilyName,
                    typeName = symbol.Name,
                    placement = placement.ToString(),
                    warnings = warnings.Count > 0 ? warnings : null
                };
            });

            return Ok($"Placed {results.Count(r => r.Value<bool>("success"))} of {results.Count} detail components in '{view.Name}'.",
                DocumentationUtils.Summarize(results));
        }

        private static FamilySymbol ResolveSymbol(List<FamilySymbol> symbols, JObject item)
        {
            if (symbols.Count == 0)
                throw new InvalidOperationException("No detail component families are loaded.");

            var typeId = DocumentationUtils.ReadId(item, "familyTypeId");
            if (typeId != null)
                return symbols.FirstOrDefault(s => s.Id.GetValue() == typeId)
                       ?? throw new ArgumentException($"familyTypeId {typeId} is not a detail component type.");

            var familyName = item.Value<string>("familyName");
            var typeName = item.Value<string>("typeName");
            if (string.IsNullOrWhiteSpace(familyName) && string.IsNullOrWhiteSpace(typeName))
                throw new ArgumentException("Provide 'familyTypeId', or 'familyName' and/or 'typeName'.");

            return symbols.FirstOrDefault(s =>
                       (string.IsNullOrWhiteSpace(familyName) || string.Equals(s.FamilyName, familyName.Trim(), StringComparison.OrdinalIgnoreCase))
                       && (string.IsNullOrWhiteSpace(typeName) || string.Equals(s.Name, typeName.Trim(), StringComparison.OrdinalIgnoreCase)))
                   ?? throw new ArgumentException(
                       $"Detail component '{familyName}: {typeName}' not found. Available: {string.Join(", ", symbols.Take(60).Select(s => $"{s.FamilyName}: {s.Name}"))}.");
        }
    }
}
