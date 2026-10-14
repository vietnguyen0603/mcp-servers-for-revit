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
                if (DocumentationUtils.ReadId(item, "copyFromElementId") is long sourceId)
                    return PlaceCopy(doc, view, item, sourceId);
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

                var warnings = ApplyParameters(instance, item);

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

        private static List<string> ApplyParameters(FamilyInstance instance, JObject item)
        {
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

            return warnings;
        }

        /// <summary>
        ///     Copies an existing detail family instance (same view: in place; other view: view-to-view
        ///     copy), optionally changes its type, then moves it: line-based items get start/end on the
        ///     copy's own plane, point-based items get location plus rotationDegrees added to the
        ///     source's rotation. Used for line-based families that NewFamilyInstance cannot create.
        /// </summary>
        private static object PlaceCopy(Document doc, View view, JObject item, long sourceId)
        {
            var source = DocumentationUtils.GetElement<FamilyInstance>(doc, sourceId)
                         ?? throw new ArgumentException($"copyFromElementId {sourceId} is not a family instance.");
            if (source.OwnerViewId == ElementId.InvalidElementId)
                throw new ArgumentException($"copyFromElementId {sourceId} is not a view-specific detail item.");

            ICollection<ElementId> copied;
            if (source.OwnerViewId == view.Id)
            {
                copied = ElementTransformUtils.CopyElement(doc, source.Id, XYZ.Zero);
            }
            else
            {
                var sourceView = doc.GetElement(source.OwnerViewId) as View
                                 ?? throw new ArgumentException($"The view of element {sourceId} was not found.");
                copied = ElementTransformUtils.CopyElements(sourceView, new List<ElementId> { source.Id }, view,
                    Transform.Identity, new CopyPasteOptions());
            }

            var instance = copied.Select(doc.GetElement).OfType<FamilyInstance>()
                               .FirstOrDefault(e => e.Symbol.Id == source.Symbol.Id)
                           ?? copied.Select(doc.GetElement).OfType<FamilyInstance>().FirstOrDefault()
                           ?? throw new InvalidOperationException($"Copying element {sourceId} returned no family instance.");

            var hasType = DocumentationUtils.ReadId(item, "familyTypeId") != null
                          || !string.IsNullOrWhiteSpace(item.Value<string>("familyName"))
                          || !string.IsNullOrWhiteSpace(item.Value<string>("typeName"));
            if (hasType)
            {
                var symbol = ResolveCopyType(doc, instance, item);
                if (symbol.Id != instance.Symbol.Id)
                {
                    if (!symbol.IsActive)
                        symbol.Activate();
                    instance.ChangeTypeId(symbol.Id);
                }
            }

            doc.Regenerate();
            string placement;
            if (instance.Location is LocationCurve)
            {
                if (item["start"] == null || item["end"] == null)
                    throw new ArgumentException($"Element {sourceId} is line-based; provide 'start' and 'end'.");
                DetailGeometry.ApplyCurve(doc, instance, item, "component");
                placement = "copied (line-based)";
            }
            else if (instance.Location is LocationPoint point)
            {
                if (item["location"] == null)
                    throw new ArgumentException($"Element {sourceId} is point-based; provide 'location'.");
                var target = DocumentationUtils.ReadPointMm(item["location"])
                             ?? throw new ArgumentException("'location' must be a point {x, y, z?} in mm.");
                target = DetailGeometry.ProjectOnto(point.Point, view.ViewDirection, target);
                point.Point = target;
                var rotation = item.Value<double?>("rotationDegrees") ?? 0;
                if (Math.Abs(rotation) > 1e-9)
                    ElementTransformUtils.RotateElement(doc, instance.Id,
                        Line.CreateBound(target, target + view.ViewDirection), rotation * Math.PI / 180);
                placement = "copied (point-based)";
            }
            else
            {
                throw new ArgumentException($"Element {sourceId} has no point or curve location.");
            }

            var warnings = ApplyParameters(instance, item);
            return new
            {
                instanceId = instance.Id.GetValue(),
                copiedFromId = sourceId,
                familyName = instance.Symbol.FamilyName,
                typeName = instance.Symbol.Name,
                placement,
                warnings = warnings.Count > 0 ? warnings : null
            };
        }

        /// <summary>Type for a copied instance: among its valid types (any category), by id or family/type name.</summary>
        private static FamilySymbol ResolveCopyType(Document doc, FamilyInstance instance, JObject item)
        {
            var valid = instance.GetValidTypes().Select(doc.GetElement).OfType<FamilySymbol>().ToList();
            var typeId = DocumentationUtils.ReadId(item, "familyTypeId");
            if (typeId != null)
                return valid.FirstOrDefault(s => s.Id.GetValue() == typeId)
                       ?? throw new ArgumentException($"familyTypeId {typeId} is not a valid type for the copied element.");
            var familyName = item.Value<string>("familyName");
            var typeName = item.Value<string>("typeName");
            return valid.FirstOrDefault(s =>
                       (string.IsNullOrWhiteSpace(familyName) || string.Equals(s.FamilyName, familyName.Trim(), StringComparison.OrdinalIgnoreCase))
                       && (string.IsNullOrWhiteSpace(typeName) || string.Equals(s.Name, typeName.Trim(), StringComparison.OrdinalIgnoreCase)))
                   ?? throw new ArgumentException(
                       $"Type '{familyName}: {typeName}' is not valid for the copied element. Valid: {string.Join(", ", valid.Take(40).Select(s => $"{s.FamilyName}: {s.Name}"))}.");
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
