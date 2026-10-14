using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Cad
{
    /// <summary>
    ///     Read-only: lists CAD imports/links, or returns one import's curves in
    ///     model coordinates (mm, 0.1 mm) grouped by layer. Lines are
    ///     [x1,y1,x2,y2], arcs [x1,y1,x2,y2,xm,ym] (start, end, mid), circles
    ///     [cx,cy,r], polylines and tessellated curves flat [x,y,x,y,...].
    /// </summary>
    public class GetCadGeometryEventHandler : JsonParameterEventHandler
    {
        private static readonly string[] AllKinds = { "lines", "arcs", "circles", "polylines", "curves", "solids" };

        public override string GetName() => "Get CAD Geometry";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var view = DocumentationUtils.GetElement<View>(doc, DocumentationUtils.ReadId(parameters, "viewId"));
            if (DocumentationUtils.ReadId(parameters, "importId") == null)
                return List(doc, view);

            var instance = CadUtils.RequireImport(doc, parameters);
            var summary = parameters.Value<bool?>("summary") ?? false;
            var maxItems = Math.Max(1, parameters.Value<int?>("maxItems") ?? 5000);
            var layerFilter = CadUtils.Matcher(CadUtils.ReadStrings(parameters["layers"]));
            var excludeFilter = CadUtils.Matcher(CadUtils.ReadStrings(parameters["excludeLayers"]));
            var region = CadRegion.Read(parameters["region"], parameters.Value<string>("regionMode"));
            var kinds = new HashSet<string>(CadUtils.ReadStrings(parameters["kinds"]).Select(k => k.ToLowerInvariant()));
            if (kinds.Count == 0)
                kinds = new HashSet<string>(AllKinds.Where(k => k != "solids"));
            var unknown = kinds.Where(k => !AllKinds.Contains(k)).ToList();
            if (unknown.Count > 0)
                throw new ArgumentException($"Unknown kinds: {string.Join(", ", unknown)}. Use {string.Join(", ", AllKinds)}.");

            var items = CadUtils.Collect(doc, instance, view)
                .Where(i => layerFilter == null || layerFilter(i.Layer))
                .Where(i => excludeFilter == null || !excludeFilter(i.Layer))
                .Where(i => kinds.Contains(KindKey(i.Kind)))
                .Where(i => region == null || region.Accepts(i))
                .ToList();

            var layers = new JArray();
            var emitted = 0;
            var truncated = false;
            var all = new CadItem();
            foreach (var group in items.GroupBy(i => i.Layer).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var extent = new CadItem();
                var counts = new JObject();
                foreach (var kind in group.GroupBy(i => KindKey(i.Kind)).OrderBy(g => Array.IndexOf(AllKinds, g.Key)))
                    counts[kind.Key] = kind.Count();
                foreach (var item in group.Where(i => i.HasExtent))
                {
                    Merge(extent, item);
                    Merge(all, item);
                }

                var layer = new JObject { ["name"] = group.Key, ["counts"] = counts };
                if (extent.HasExtent)
                {
                    layer["extents"] = Extents(extent);
                    layer["zRange"] = new JArray(CadUtils.Mm(extent.MinZ), CadUtils.Mm(extent.MaxZ));
                }

                if (!summary)
                {
                    var arrays = new Dictionary<string, JArray>();
                    foreach (var item in group)
                    {
                        if (item.Kind == "solid" || item.Kind == "mesh" || item.Kind == "point")
                            continue;
                        if (emitted >= maxItems)
                        {
                            truncated = true;
                            break;
                        }

                        var key = KindKey(item.Kind);
                        if (!arrays.TryGetValue(key, out var array))
                            arrays[key] = array = new JArray();
                        array.Add(Encode(item));
                        emitted++;
                    }

                    foreach (var key in AllKinds.Where(arrays.ContainsKey))
                        layer[key] = arrays[key];
                }

                layers.Add(layer);
            }

            var response = new JObject
            {
                ["import"] = JObject.FromObject(CadUtils.Describe(doc, instance)),
                ["transform"] = JObject.FromObject(CadUtils.TransformInfo(instance.GetTotalTransform())),
                ["units"] = "mm, model coordinates (instance and block transforms applied), rounded to 0.1",
                ["format"] = "lines [x1,y1,x2,y2]; arcs [x1,y1,x2,y2,xm,ym] (start,end,mid); circles [cx,cy,r]; polylines/curves [x,y,x,y,...]; solids/hatches counted only",
                ["note"] = CadUtils.TextNote,
                ["itemCount"] = items.Count,
                ["layerCount"] = layers.Count,
                ["extents"] = all.HasExtent ? Extents(all) : null,
                ["layers"] = layers
            };
            if (!summary)
            {
                response["returnedItems"] = emitted;
                response["truncated"] = truncated;
            }

            return Ok($"{items.Count} items on {layers.Count} layers of '{CadUtils.ImportName(instance)}'" +
                      (truncated ? $" (truncated to {maxItems})." : "."), response);
        }

        private static AIResult<object> List(Document doc, View view)
        {
            var collector = view != null ? new FilteredElementCollector(doc, view.Id) : new FilteredElementCollector(doc);
            var imports = collector.OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                .OrderBy(i => i.Id.GetValue())
                .Select(i => CadUtils.Describe(doc, i))
                .Take(1000)
                .ToList();
            return Ok($"{imports.Count} CAD imports/links{(view != null ? $" visible in '{view.Name}'" : "")}. Pass 'importId' to read geometry.",
                new { count = imports.Count, imports, note = CadUtils.TextNote });
        }

        private static string KindKey(string kind)
        {
            switch (kind)
            {
                case "line": return "lines";
                case "arc": return "arcs";
                case "circle": return "circles";
                case "polyline": return "polylines";
                case "curve": return "curves";
                default: return "solids";
            }
        }

        private static JArray Encode(CadItem item)
        {
            switch (item.Kind)
            {
                case "line":
                    return Flat(item.Curve.GetEndPoint(0), item.Curve.GetEndPoint(1));
                case "arc":
                    return Flat(item.Curve.GetEndPoint(0), item.Curve.GetEndPoint(1), item.Curve.Evaluate(0.5, true));
                case "circle":
                    var arc = (Arc)item.Curve;
                    return new JArray(CadUtils.Mm(arc.Center.X), CadUtils.Mm(arc.Center.Y), CadUtils.Mm(arc.Radius));
                default:
                    return Flat(item.Points.ToArray());
            }
        }

        private static JArray Flat(params XYZ[] points)
        {
            var array = new JArray();
            foreach (var p in points)
            {
                array.Add(CadUtils.Mm(p.X));
                array.Add(CadUtils.Mm(p.Y));
            }

            return array;
        }

        private static void Merge(CadItem target, CadItem item)
        {
            target.Grow(new XYZ(item.MinX, item.MinY, item.MinZ));
            target.Grow(new XYZ(item.MaxX, item.MaxY, item.MaxZ));
        }

        private static JObject Extents(CadItem e)
        {
            return new JObject
            {
                ["minX"] = CadUtils.Mm(e.MinX),
                ["minY"] = CadUtils.Mm(e.MinY),
                ["maxX"] = CadUtils.Mm(e.MaxX),
                ["maxY"] = CadUtils.Mm(e.MaxY)
            };
        }
    }
}
