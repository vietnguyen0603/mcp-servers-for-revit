using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Library
{
    /// <summary>
    ///     Signatures used by merge_duplicate_families to decide that two types are the same:
    ///     <list type="bullet">
    ///         <item>
    ///             Type parameters: every type parameter value except name/identity ones
    ///             (type/family name, category, image, IFC GUIDs, workset ...). Doubles are
    ///             rounded to 1e-6 internal units; element-id values are compared by the
    ///             referenced element's category + family + name, not the raw id.
    ///         </item>
    ///         <item>
    ///             Type geometry (model families): solids (volume, face/edge counts), curves
    ///             (type, length, end points) and nested geometry from FamilySymbol.get_Geometry.
    ///         </item>
    ///         <item>
    ///             Family drawing (loadable families): the family is opened read-only with
    ///             EditFamily (closed without saving) and every drawn element is reduced to a
    ///             token - lines (category, line style, curve type, length, end points), filled /
    ///             masking regions (type, loops, boundary length, extents), text and labels
    ///             (text, position, type), solid/void forms (volume, faces, extents), nested
    ///             instances (category + placement transform; not their contents), imports
    ///             (extents) and any other model/annotation element (class, category, extents).
    ///             Reference planes, dimensions and views are ignored. Coordinates are rounded
    ///             to 1e-4 ft (0.03 mm), so the same drawing placed differently is a mismatch.
    ///         </item>
    ///         <item>System families (walls, floors ...): compound structure layers.</item>
    ///     </list>
    ///     Each signature is the SHA-1 of the sorted tokens; equal hashes mean "same".
    /// </summary>
    public static class FamilyFingerprint
    {
        private static readonly HashSet<int> IdentityParameters = BuildIdentitySet();

        private static HashSet<int> BuildIdentitySet()
        {
            var names = new[]
            {
                "SYMBOL_NAME_PARAM", "ALL_MODEL_TYPE_NAME", "SYMBOL_FAMILY_NAME_PARAM", "ALL_MODEL_FAMILY_NAME",
                "SYMBOL_FAMILY_AND_TYPE_NAMES_PARAM", "ELEM_FAMILY_PARAM", "ELEM_FAMILY_AND_TYPE_PARAM", "ELEM_TYPE_PARAM",
                "ELEM_CATEGORY_PARAM", "ELEM_CATEGORY_PARAM_MT", "SYMBOL_ID_PARAM", "ID_PARAM", "EDITED_BY",
                "ALL_MODEL_TYPE_IMAGE", "ALL_MODEL_IMAGE", "DESIGN_OPTION_ID", "ELEM_PARTITION_PARAM",
                "IFC_GUID", "IFC_TYPE_GUID", "DATUM_TEXT", "VIEW_FAMILY_AND_TYPE_SCHEDULES", "VIEW_FAMILY_SCHEDULES",
                "VIEW_TYPE_SCHEDULES", "SYMBOL_FAMILY_NAME_PARAM"
            };
            var set = new HashSet<int>();
            foreach (var name in names)
                if (Enum.TryParse(name, out BuiltInParameter bip))
                    set.Add((int)bip);
            return set;
        }

        /// <summary>Type parameter values keyed by "bip:", "guid:" or "name:" + definition.</summary>
        public static SortedDictionary<string, KeyValuePair<string, string>> TypeParameters(Element type, ISet<string> ignoreNames)
        {
            var values = new SortedDictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
            foreach (Parameter parameter in type.Parameters)
            {
                var definition = parameter.Definition;
                if (definition == null || ignoreNames.Contains(definition.Name))
                    continue;

                string key;
                if (definition is InternalDefinition internalDefinition && internalDefinition.BuiltInParameter != BuiltInParameter.INVALID)
                {
                    if (IdentityParameters.Contains((int)internalDefinition.BuiltInParameter))
                        continue;
                    key = "bip:" + (int)internalDefinition.BuiltInParameter;
                }
                else if (parameter.IsShared)
                {
                    key = "guid:" + parameter.GUID;
                }
                else
                {
                    key = "name:" + definition.Name;
                }

                var value = ValueOf(type.Document, parameter);
                while (values.ContainsKey(key))
                    key += "'";
                values[key] = new KeyValuePair<string, string>(definition.Name, value);
            }

            return values;
        }

        public static string Hash(SortedDictionary<string, KeyValuePair<string, string>> values)
        {
            return Hash(values.Select(v => v.Key + "=" + v.Value.Value));
        }

        /// <summary>Names of the parameters whose values differ (or exist on one side only).</summary>
        public static List<string> Differences(SortedDictionary<string, KeyValuePair<string, string>> a,
            SortedDictionary<string, KeyValuePair<string, string>> b)
        {
            var names = new List<string>();
            foreach (var key in a.Keys.Union(b.Keys))
            {
                var inA = a.TryGetValue(key, out var va);
                var inB = b.TryGetValue(key, out var vb);
                if (!inA || !inB)
                    names.Add((inA ? va.Key : vb.Key) + (inA ? " (only on keep)" : " (only on merge)"));
                else if (va.Value != vb.Value)
                    names.Add($"{va.Key}: '{va.Value}' vs '{vb.Value}'");
            }

            return names;
        }

        private static string ValueOf(Document doc, Parameter parameter)
        {
            if (!parameter.HasValue)
                return "<none>";
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                    return Num(parameter.AsDouble(), 6);
                case StorageType.Integer:
                    return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                case StorageType.String:
                    return parameter.AsString() ?? "";
                case StorageType.ElementId:
                    var id = parameter.AsElementId();
                    if (id == null || id == ElementId.InvalidElementId)
                        return "<none>";
                    if (id.GetValue() < 0)
                        return "builtin:" + id.GetValue();
                    var element = doc.GetElement(id);
                    if (element == null)
                        return "missing";
                    if (element is FamilySymbol symbol)
                        return $"symbol|{symbol.Category?.Name}|{symbol.FamilyName}|{symbol.Name}";
                    return $"{element.GetType().Name}|{element.Category?.Name}|{element.Name}";
                default:
                    return "";
            }
        }

        /// <summary>Hash of FamilySymbol.get_Geometry (empty string when the type has no geometry).</summary>
        public static string TypeGeometry(ElementType type)
        {
            var tokens = new List<string>();
            try
            {
                if (type is FamilySymbol symbol)
                {
                    var geometry = symbol.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
                    if (geometry != null)
                        Walk(geometry, tokens, 0);
                }
                else if (type is HostObjAttributes host)
                {
                    var structure = host.GetCompoundStructure();
                    if (structure != null)
                        foreach (var layer in structure.GetLayers())
                            tokens.Add($"layer|{layer.Function}|{Num(layer.Width, 6)}|{type.Document.GetElement(layer.MaterialId)?.Name}");
                }
            }
            catch (Exception ex)
            {
                tokens.Add("error|" + ex.GetType().Name);
            }

            return tokens.Count == 0 ? "" : Hash(tokens);
        }

        private static void Walk(GeometryElement geometry, List<string> tokens, int depth)
        {
            foreach (var obj in geometry)
            {
                switch (obj)
                {
                    case Solid solid when solid.Volume > 1e-9 || solid.Faces.Size > 0:
                        tokens.Add($"solid|{depth}|{Num(solid.Volume, 6)}|{solid.Faces.Size}|{solid.Edges.Size}");
                        break;
                    case Curve curve:
                        tokens.Add("curve|" + depth + "|" + CurveToken(curve));
                        break;
                    case Mesh mesh:
                        tokens.Add($"mesh|{depth}|{mesh.NumTriangles}");
                        break;
                    case PolyLine polyLine:
                        tokens.Add($"polyline|{depth}|{polyLine.NumberOfCoordinates}");
                        break;
                    case GeometryInstance instance when depth < 4:
                        tokens.Add($"instance|{depth}|{Pt(instance.Transform.Origin)}|{Pt(instance.Transform.BasisX)}");
                        var nested = instance.GetSymbolGeometry();
                        if (nested != null)
                            Walk(nested, tokens, depth + 1);
                        break;
                }
            }
        }

        /// <summary>
        ///     Opens the family read-only and hashes its drawn elements (see class summary).
        ///     Returns null with <paramref name="error" /> set when the family can't be opened.
        /// </summary>
        public static string FamilyDrawing(Document doc, Family family, out DrawingSummary summary, out string error)
        {
            summary = new DrawingSummary();
            error = null;
            if (family.IsInPlace)
            {
                error = "In-place family.";
                return null;
            }

            if (!family.IsEditable)
            {
                error = "Family is not editable.";
                return null;
            }

            Document familyDoc = null;
            try
            {
                familyDoc = doc.EditFamily(family);
                var tokens = new List<string>();
                var options = new Options { DetailLevel = ViewDetailLevel.Fine };
                foreach (var element in new FilteredElementCollector(familyDoc).WhereElementIsNotElementType())
                {
                    var token = ElementToken(familyDoc, element, options, summary);
                    if (token != null)
                        tokens.Add(token);
                }

                tokens.Add("category|" + family.FamilyCategory?.Id.GetValue());
                return Hash(tokens);
            }
            catch (Exception ex)
            {
                error = "Could not open the family: " + ex.Message;
                return null;
            }
            finally
            {
                familyDoc?.Close(false);
            }
        }

        private static string ElementToken(Document familyDoc, Element element, Options options, DrawingSummary summary)
        {
            switch (element)
            {
                case View _:
                case ReferencePlane _:
                case Dimension _:
                case SketchPlane _:
                case Level _:
                case Family _:
                case GraphicsStyle _:
                case Material _:
                    return null;
                case CurveElement curveElement:
                {
                    var curve = curveElement.GeometryCurve;
                    if (curve == null)
                        return null;
                    summary.Lines++;
                    return $"line|{element.Category?.Name}|{curveElement.LineStyle?.Name}|{CurveToken(curve)}";
                }
                case FilledRegion region:
                {
                    summary.Regions++;
                    var loops = region.GetBoundaries();
                    var length = 0.0;
                    var points = new List<XYZ>();
                    foreach (var loop in loops)
                        foreach (var curve in loop)
                        {
                            length += curve.Length;
                            points.Add(curve.GetEndPoint(0));
                        }

                    return $"region|{familyDoc.GetElement(region.GetTypeId())?.Name}|{region.IsMasking}|{loops.Count}|{Num(length, 4)}|{Extents(points)}";
                }
                case TextElement text:
                    summary.Texts++;
                    return $"text|{element.Category?.Name}|{text.Text}|{Pt(text.Coord)}|{familyDoc.GetElement(text.GetTypeId())?.Name}";
                case GenericForm form:
                {
                    summary.Forms++;
                    var tokens = new List<string>();
                    var geometry = form.get_Geometry(options);
                    if (geometry != null)
                        Walk(geometry, tokens, 0);
                    tokens.Sort(StringComparer.Ordinal);
                    return $"form|{form.GetType().Name}|{form.IsSolid}|{string.Join(";", tokens)}";
                }
                case FamilyInstance instance:
                {
                    summary.Nested++;
                    var transform = instance.GetTransform();
                    return $"nested|{element.Category?.Name}|{Pt(transform.Origin)}|{Pt(transform.BasisX)}|{Pt(transform.BasisY)}";
                }
                case ImportInstance import:
                    summary.Imports++;
                    return $"import|{Box(import.get_BoundingBox(null))}";
            }

            var category = element.Category;
            if (category == null || (category.CategoryType != CategoryType.Model && category.CategoryType != CategoryType.Annotation))
                return null;
            var box = element.get_BoundingBox(null);
            if (box == null)
                return null;
            summary.Other++;
            return $"element|{element.GetType().Name}|{category.Name}|{Box(box)}";
        }

        private static string CurveToken(Curve curve)
        {
            if (!curve.IsBound)
                return $"{curve.GetType().Name}|unbound|{Num(curve.Length, 4)}";
            return $"{curve.GetType().Name}|{Num(curve.Length, 4)}|{Pt(curve.GetEndPoint(0))}|{Pt(curve.GetEndPoint(1))}";
        }

        private static string Extents(List<XYZ> points)
        {
            if (points.Count == 0)
                return "";
            return $"{Num(points.Min(p => p.X), 4)},{Num(points.Min(p => p.Y), 4)}..{Num(points.Max(p => p.X), 4)},{Num(points.Max(p => p.Y), 4)}";
        }

        private static string Box(BoundingBoxXYZ box) => box == null ? "" : Pt(box.Min) + ".." + Pt(box.Max);

        private static string Pt(XYZ p) => p == null ? "" : $"{Num(p.X, 4)},{Num(p.Y, 4)},{Num(p.Z, 4)}";

        private static string Num(double value, int digits)
        {
            var rounded = Math.Round(value, digits);
            if (Math.Abs(rounded) < Math.Pow(10, -digits) / 2)
                rounded = 0;
            return rounded.ToString("0.######", CultureInfo.InvariantCulture);
        }

        public static string Hash(IEnumerable<string> tokens)
        {
            var sorted = tokens.OrderBy(t => t, StringComparer.Ordinal);
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", sorted)));
                return BitConverter.ToString(bytes, 0, 8).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>Counts of the drawn elements found in a family (reported to the user).</summary>
        public class DrawingSummary
        {
            public int Lines { get; set; }
            public int Regions { get; set; }
            public int Texts { get; set; }
            public int Forms { get; set; }
            public int Nested { get; set; }
            public int Imports { get; set; }
            public int Other { get; set; }

            public override string ToString() =>
                $"{Lines} lines, {Regions} regions, {Texts} texts/labels, {Forms} forms, {Nested} nested, {Imports} imports, {Other} other";
        }
    }
}
