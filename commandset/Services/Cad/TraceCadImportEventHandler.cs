using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services.AnnotationComponents;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Cad
{
    /// <summary>
    ///     Re-draws a CAD import's curves as native detail lines (in the view
    ///     plane) or model lines (on horizontal sketch planes), mapping layers
    ///     to line styles, and optionally deletes the import. Revit cannot
    ///     explode imports through the API; this is the replacement.
    /// </summary>
    public class TraceCadImportEventHandler : JsonParameterEventHandler
    {
        private const int MaxReturnedIds = 2000;
        private const double PlaneTolerance = 1e-4; // feet

        public override string GetName() => "Trace CAD Import";

        private sealed class StyleChoice
        {
            public GraphicsStyle Style;
            public string Source;
        }

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var instance = CadUtils.RequireImport(doc, parameters);
            var view = DocumentationUtils.GetElement<View>(doc, DocumentationUtils.ReadId(parameters, "viewId"));
            if (view == null && instance.ViewSpecific)
                view = doc.GetElement(instance.OwnerViewId) as View;
            view = view ?? uiDoc.ActiveView;
            if (view == null || view.IsTemplate)
                throw new ArgumentException("A target view is required.");

            var model = string.Equals(parameters.Value<string>("lineKind"), "model", StringComparison.OrdinalIgnoreCase);
            if (!model && (view.ViewType == ViewType.ThreeD || view.ViewType == ViewType.Schedule))
                throw new ArgumentException($"Detail lines cannot be drawn in '{view.Name}' ({view.ViewType}); use lineKind 'model' or a 2D view.");
            if (model && view.ViewType == ViewType.DraftingView)
                throw new ArgumentException("Model lines cannot be drawn in a drafting view; use lineKind 'detail'.");

            var dryRun = parameters.Value<bool?>("dryRun") ?? true;
            var deleteImport = parameters.Value<bool?>("deleteImport") ?? false;
            var visibleOnly = parameters.Value<bool?>("visibleLayersOnly") ?? true;
            var maxCurves = Math.Max(1, parameters.Value<int?>("maxCurves") ?? 20000);
            var minLength = Math.Max(doc.Application.ShortCurveTolerance,
                DocumentationUtils.MmToFeet(parameters.Value<double?>("minLength") ?? 0));
            var layerFilter = CadUtils.Matcher(CadUtils.ReadStrings(parameters["layers"]));
            var excludeFilter = CadUtils.Matcher(CadUtils.ReadStrings(parameters["excludeLayers"]));
            var region = CadRegion.Read(parameters["region"], parameters.Value<string>("regionMode"));
            var styleMap = ReadStyleMap(parameters["lineStyleMap"]);
            var defaultStyleName = parameters.Value<string>("defaultLineStyle");
            var defaultStyle = string.IsNullOrWhiteSpace(defaultStyleName)
                ? null
                : DetailGeometry.ResolveLineStyle(doc, defaultStyleName);
            var lineStyles = LineStyles(doc);

            var hiddenLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (visibleOnly)
            {
                foreach (var layer in CadUtils.Layers(instance))
                {
                    var category = CadUtils.LayerCategory(instance, layer);
                    if (category != null && view.GetCategoryHidden(category.Id))
                        hiddenLayers.Add(layer);
                }
            }

            var items = CadUtils.Collect(doc, instance, view)
                .Where(i => i.Kind != "solid" && i.Kind != "mesh" && i.Kind != "point")
                .Where(i => layerFilter == null || layerFilter(i.Layer))
                .Where(i => excludeFilter == null || !excludeFilter(i.Layer))
                .Where(i => !hiddenLayers.Contains(i.Layer))
                .Where(i => region == null || region.Accepts(i))
                .ToList();

            // Curves to draw per layer (polylines split into lines)
            var skipped = new Dictionary<string, int>();
            var perLayer = new SortedDictionary<string, List<Curve>>(StringComparer.OrdinalIgnoreCase);
            var total = 0;
            var truncated = false;
            foreach (var item in items)
            {
                foreach (var curve in Curves(item, minLength, skipped))
                {
                    if (total >= maxCurves)
                    {
                        truncated = true;
                        break;
                    }

                    if (!perLayer.TryGetValue(item.Layer, out var list))
                        perLayer[item.Layer] = list = new List<Curve>();
                    list.Add(curve);
                    total++;
                }

                if (truncated)
                    break;
            }

            var warnings = new List<string>();
            var choices = new Dictionary<string, StyleChoice>(StringComparer.OrdinalIgnoreCase);
            foreach (var layer in perLayer.Keys)
                choices[layer] = ChooseStyle(layer, styleMap, lineStyles, defaultStyle, warnings);

            var layerReport = perLayer.Select(kv => new
            {
                layer = kv.Key,
                curves = kv.Value.Count,
                lineStyle = choices[kv.Key].Style?.Name ?? "(Revit default)",
                styleSource = choices[kv.Key].Source
            }).ToList();

            var common = new
            {
                import = CadUtils.Describe(doc, instance),
                viewId = view.Id.GetValue(),
                viewName = view.Name,
                lineKind = model ? "model" : "detail",
                hiddenLayersSkipped = hiddenLayers.OrderBy(n => n).ToList(),
                layers = layerReport
            };

            if (dryRun)
                return Ok($"Dry run: would create {total} {(model ? "model" : "detail")} lines on {perLayer.Count} layers in '{view.Name}'" +
                          $"{(deleteImport ? " and delete the import" : "")}.", new
                {
                    dryRun = true,
                    curveCount = total,
                    truncated,
                    skipped,
                    warnings,
                    common.import,
                    common.viewId,
                    common.viewName,
                    common.lineKind,
                    common.hiddenLayersSkipped,
                    common.layers,
                    note = CadUtils.TextNote
                });

            var created = new List<long>();
            var failures = new Dictionary<string, int>();
            var deleted = false;
            using (var transaction = DocumentationUtils.StartTransaction(doc, "MCP: Trace CAD Import"))
            {
                var plane = ViewPlane(view);
                var sketchPlanes = new Dictionary<long, SketchPlane>();
                foreach (var kv in perLayer)
                {
                    var style = choices[kv.Key].Style;
                    foreach (var source in kv.Value)
                    {
                        try
                        {
                            CurveElement element;
                            if (model)
                            {
                                var sketch = HorizontalSketchPlane(doc, source, sketchPlanes);
                                if (sketch == null)
                                {
                                    Count(failures, "not horizontal (model lines)");
                                    continue;
                                }

                                element = doc.Create.NewModelCurve(source, sketch);
                            }
                            else
                            {
                                var flat = OntoPlane(source, plane);
                                if (flat == null)
                                {
                                    Count(failures, "not parallel to the view plane");
                                    continue;
                                }

                                element = doc.Create.NewDetailCurve(view, flat);
                            }

                            if (style != null && element != null)
                                element.LineStyle = style;
                            if (element != null)
                                created.Add(element.Id.GetValue());
                        }
                        catch (Exception ex)
                        {
                            Count(failures, ex.Message);
                        }
                    }
                }

                if (deleteImport && created.Count > 0)
                {
                    if (instance.Pinned)
                        instance.Pinned = false;
                    doc.Delete(instance.Id);
                    deleted = true;
                }

                var status = transaction.Commit();
                if (status != TransactionStatus.Committed)
                    return Fail($"Trace was not committed ({status}).");
            }

            return Ok($"Created {created.Count} {(model ? "model" : "detail")} lines from {perLayer.Count} layers in '{view.Name}'" +
                      $"{(deleted ? "; import deleted" : "")}.", new
            {
                dryRun = false,
                createdCount = created.Count,
                createdIds = created.Take(MaxReturnedIds).ToList(),
                createdIdsTruncated = created.Count > MaxReturnedIds,
                importDeleted = deleted,
                truncated,
                skipped,
                failures,
                warnings,
                common.import,
                common.viewId,
                common.viewName,
                common.lineKind,
                common.layers
            });
        }

        private static List<KeyValuePair<System.Text.RegularExpressions.Regex, string>> ReadStyleMap(JToken token)
        {
            var map = new List<KeyValuePair<System.Text.RegularExpressions.Regex, string>>();
            if (token is JObject obj)
            {
                // Exact names first, then wildcard patterns in the given order
                foreach (var property in obj.Properties().OrderBy(p => p.Name.Contains("*") || p.Name.Contains("?") ? 1 : 0))
                    map.Add(new KeyValuePair<System.Text.RegularExpressions.Regex, string>(
                        CadUtils.WildcardRegex(property.Name), property.Value.Value<string>()));
            }

            return map;
        }

        private static Dictionary<string, GraphicsStyle> LineStyles(Document doc)
        {
            var styles = new Dictionary<string, GraphicsStyle>(StringComparer.OrdinalIgnoreCase);
            var lines = Category.GetCategory(doc, BuiltInCategory.OST_Lines);
            foreach (Category sub in lines.SubCategories)
            {
                var style = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                if (style != null && !styles.ContainsKey(sub.Name))
                    styles[sub.Name] = style;
            }

            return styles;
        }

        private static StyleChoice ChooseStyle(string layer,
            List<KeyValuePair<System.Text.RegularExpressions.Regex, string>> map,
            Dictionary<string, GraphicsStyle> styles, GraphicsStyle fallback, List<string> warnings)
        {
            foreach (var entry in map)
            {
                if (!entry.Key.IsMatch(layer))
                    continue;
                var actual = DetailGeometry.MatchLineStyleName(styles.Keys, entry.Value);
                if (actual != null)
                    return new StyleChoice { Style = styles[actual], Source = "map" };
                warnings.Add($"Line style '{entry.Value}' (mapped from layer '{layer}') not found.");
                break;
            }

            if (styles.TryGetValue(layer, out var same))
                return new StyleChoice { Style = same, Source = "layerName" };
            return fallback != null
                ? new StyleChoice { Style = fallback, Source = "default" }
                : new StyleChoice { Style = null, Source = "revitDefault" };
        }

        private static IEnumerable<Curve> Curves(CadItem item, double minLength, Dictionary<string, int> skipped)
        {
            if (item.Kind == "polyline")
            {
                for (var i = 0; i + 1 < item.Points.Count; i++)
                {
                    var a = item.Points[i];
                    var b = item.Points[i + 1];
                    if (a.DistanceTo(b) < minLength)
                    {
                        Count(skipped, "too short");
                        continue;
                    }

                    yield return Line.CreateBound(a, b);
                }

                yield break;
            }

            if (item.Curve == null)
                yield break;
            if (item.Kind == "circle" && item.Curve is Arc circle)
            {
                // Full circles become two half arcs (detail/model curves must be bound)
                yield return Arc.Create(circle.Center, circle.Radius, 0, Math.PI, circle.XDirection, circle.YDirection);
                yield return Arc.Create(circle.Center, circle.Radius, Math.PI, 2 * Math.PI, circle.XDirection, circle.YDirection);
                yield break;
            }

            if (item.Curve.IsBound && item.Curve.Length < minLength)
            {
                Count(skipped, "too short");
                yield break;
            }

            yield return item.Curve;
        }

        private static Plane ViewPlane(View view)
        {
            var origin = view.SketchPlane?.GetPlane()?.Origin ?? view.Origin;
            return Plane.CreateByNormalAndOrigin(view.ViewDirection, origin);
        }

        /// <summary>Moves a curve lying parallel to the view plane into it; null when it is not parallel.</summary>
        private static Curve OntoPlane(Curve curve, Plane plane)
        {
            var normal = plane.Normal;
            double Distance(XYZ p) => (p - plane.Origin).DotProduct(normal);
            var samples = curve.IsBound
                ? new[] { curve.GetEndPoint(0), curve.GetEndPoint(1), curve.Evaluate(0.5, true) }
                : new[] { curve.Evaluate(0, false), curve.Evaluate(Math.PI / 2, false), curve.Evaluate(Math.PI, false) };
            var d = Distance(samples[0]);
            if (samples.Any(p => Math.Abs(Distance(p) - d) > PlaneTolerance))
                return null;
            return Math.Abs(d) < 1e-9 ? curve : curve.CreateTransformed(Transform.CreateTranslation(normal.Multiply(-d)));
        }

        private static SketchPlane HorizontalSketchPlane(Document doc, Curve curve, Dictionary<long, SketchPlane> cache)
        {
            var samples = curve.IsBound
                ? new[] { curve.GetEndPoint(0), curve.GetEndPoint(1), curve.Evaluate(0.5, true) }
                : new[] { curve.Evaluate(0, false), curve.Evaluate(Math.PI / 2, false), curve.Evaluate(Math.PI, false) };
            var z = samples[0].Z;
            if (samples.Any(p => Math.Abs(p.Z - z) > PlaneTolerance))
                return null;
            var key = (long)Math.Round(z * DocumentationUtils.MmPerFoot * 10);
            if (!cache.TryGetValue(key, out var plane))
            {
                plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z)));
                cache[key] = plane;
            }

            return plane;
        }

        private static void Count(Dictionary<string, int> counts, string key)
        {
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }
    }
}
