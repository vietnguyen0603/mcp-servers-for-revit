using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
    ///             token under an aspect - lines (category, line style, curve type, length, end
    ///             points), regions (type, loops, perimeter, extents), texts/labels (text,
    ///             position, type), forms (class, solid/void, extrusion depth, bounding box,
    ///             volume/faces/edges), nested instances (category + placement; not their
    ///             contents), imports (extents) and other model/annotation elements (class,
    ///             category, extents). Reference planes, dimensions, views, sketches, elevation
    ///             markers and origins are ignored.
    ///         </item>
    ///         <item>System families (walls, floors ...): compound structure layers.</item>
    ///     </list>
    ///     Tokens never contain element ids, unique ids or the family/type's own name, are written in mm
    ///     and are direction-independent (curve end points in a stable order). Comparison is an
    ///     order-independent multiset match where every number may differ by 0.05 (mm / cm3) + 1e-5
    ///     relative, so floating-point noise doesn't separate identical families. The hash of the
    ///     sorted tokens is only a fast path for "identical".
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

        /// <summary>
        ///     Tokens of FamilySymbol.get_Geometry (solids, curves, nested instances) or of a system type's
        ///     compound structure. Empty when the type has no geometry. Compare with <see cref="SameTokens" />.
        /// </summary>
        public static List<string> TypeGeometry(ElementType type)
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
                            tokens.Add($"layer {layer.Function} w={Mm(layer.Width)} material='{type.Document.GetElement(layer.MaterialId)?.Name}'");
                }
            }
            catch (Exception ex)
            {
                tokens.Add("error " + ex.GetType().Name);
            }

            tokens.Sort(StringComparer.Ordinal);
            return tokens;
        }

        private static void Walk(GeometryElement geometry, List<string> tokens, int depth)
        {
            foreach (var obj in geometry)
            {
                switch (obj)
                {
                    case Solid solid when solid.Volume > 1e-9 || solid.Faces.Size > 0:
                        tokens.Add($"solid d{depth} vol={Cm3(solid.Volume)}cm3 faces={solid.Faces.Size} edges={solid.Edges.Size}");
                        break;
                    case Curve curve:
                        tokens.Add($"curve d{depth} " + CurveToken(curve));
                        break;
                    case Mesh mesh:
                        tokens.Add($"mesh d{depth} triangles={mesh.NumTriangles}");
                        break;
                    case PolyLine polyLine:
                        tokens.Add($"polyline d{depth} points={polyLine.NumberOfCoordinates}");
                        break;
                    case GeometryInstance instance when depth < 4:
                        tokens.Add($"instance d{depth} at {Pt(instance.Transform.Origin)} x={Dir(instance.Transform.BasisX)}");
                        var nested = instance.GetSymbolGeometry();
                        if (nested != null)
                            Walk(nested, tokens, depth + 1);
                        break;
                }
            }
        }

        /// <summary>
        ///     Opens the family read-only and reduces its drawn elements to tokens grouped by aspect
        ///     (see class summary). Returns null with <paramref name="error" /> set when the family
        ///     can't be opened.
        /// </summary>
        public static Drawing FamilyDrawing(Document doc, Family family, out string error)
        {
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
                var drawing = new Drawing();
                var options = new Options { DetailLevel = ViewDetailLevel.Fine };
                foreach (var element in new FilteredElementCollector(familyDoc).WhereElementIsNotElementType())
                {
                    string token, aspect;
                    try
                    {
                        token = ElementToken(familyDoc, element, options, drawing.Summary, out aspect);
                    }
                    catch (Exception ex)
                    {
                        aspect = "other";
                        token = $"{element.GetType().Name}/{element.Category?.Name} unreadable ({ex.GetType().Name})";
                    }

                    if (token != null)
                        drawing.Add(aspect, token);
                }

                drawing.Add("category", family.FamilyCategory?.Name ?? "<none>");
                drawing.Seal();
                return drawing;
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

        /// <summary>
        ///     Classes that are not part of the drawing: datums, views and their markers, sketches (their
        ///     lines are counted as lines), constraints, origins, project settings. Matched by name so
        ///     classes missing from older Revit versions don't break the build.
        /// </summary>
        private static readonly HashSet<string> IgnoredClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "ElevationMarker", "Sketch", "SketchBase", "InternalOrigin", "BasePoint", "SunAndShadowSettings",
            "ProjectInfo", "Viewport", "ConstraintElement", "SketchPlane", "ReferencePlane", "Level", "Family",
            "GraphicsStyle", "Material"
        };

        private static string ElementToken(Document familyDoc, Element element, Options options, DrawingSummary summary, out string aspect)
        {
            aspect = "other";
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
                    aspect = "lines";
                    return $"{element.Category?.Name}/{curveElement.LineStyle?.Name} {CurveToken(curve)}";
                }
                case FilledRegion region:
                {
                    summary.Regions++;
                    aspect = "regions";
                    var loops = region.GetBoundaries();
                    var length = 0.0;
                    var points = new List<XYZ>();
                    foreach (var loop in loops)
                        foreach (var curve in loop)
                        {
                            length += curve.Length;
                            points.Add(curve.GetEndPoint(0));
                        }

                    return $"{(region.IsMasking ? "masking" : "filled")} '{familyDoc.GetElement(region.GetTypeId())?.Name}' loops={loops.Count} perimeter={Mm(length)} extents={Extents(points)}";
                }
                case TextElement text:
                    summary.Texts++;
                    aspect = "texts";
                    return $"{element.Category?.Name} '{text.Text}' at {Pt(text.Coord)} type='{familyDoc.GetElement(text.GetTypeId())?.Name}'";
                case GenericForm form:
                {
                    summary.Forms++;
                    aspect = "forms";
                    var tokens = new List<string>();
                    var geometry = form.get_Geometry(options);
                    if (geometry != null)
                        Walk(geometry, tokens, 0);
                    tokens.Sort(StringComparer.Ordinal);
                    var depth = form is Extrusion extrusion ? $" depth={Mm(Math.Abs(extrusion.EndOffset - extrusion.StartOffset))}" : "";
                    return $"{form.GetType().Name} {(form.IsSolid ? "solid" : "void")}{depth} box={Box(form.get_BoundingBox(null))} [{string.Join("; ", tokens)}]";
                }
                case FamilyInstance instance:
                {
                    summary.Nested++;
                    aspect = "nested";
                    var transform = instance.GetTransform();
                    return $"{element.Category?.Name} at {Pt(transform.Origin)} x={Dir(transform.BasisX)} y={Dir(transform.BasisY)}";
                }
                case ImportInstance import:
                    summary.Imports++;
                    aspect = "imports";
                    return $"import box={Box(import.get_BoundingBox(null))}";
            }

            var className = element.GetType().Name;
            if (IgnoredClasses.Contains(className))
                return null;
            var category = element.Category;
            if (category == null || (category.CategoryType != CategoryType.Model && category.CategoryType != CategoryType.Annotation))
                return null;
            var box = element.get_BoundingBox(null);
            if (box == null)
                return null;
            summary.AddOther(className + "/" + category.Name);
            return $"{className}/{category.Name} box={Box(box)}";
        }

        /// <summary>Direction-independent: the end points are written in a stable order.</summary>
        private static string CurveToken(Curve curve)
        {
            if (!curve.IsBound)
                return $"{curve.GetType().Name} unbound L={Mm(curve.Length)}";
            var p0 = curve.GetEndPoint(0);
            var p1 = curve.GetEndPoint(1);
            if (Before(p1, p0))
            {
                var swap = p0;
                p0 = p1;
                p1 = swap;
            }

            return $"{curve.GetType().Name} L={Mm(curve.Length)} {Pt(p0)} to {Pt(p1)}";
        }

        /// <summary>Point order with a 0.5 mm dead band so noise can't swap the ends of an axis-aligned line.</summary>
        private static bool Before(XYZ a, XYZ b)
        {
            const double band = 0.5 / 304.8;
            if (Math.Abs(a.X - b.X) > band) return a.X < b.X;
            if (Math.Abs(a.Y - b.Y) > band) return a.Y < b.Y;
            return a.Z < b.Z;
        }

        private static string Extents(List<XYZ> points)
        {
            if (points.Count == 0)
                return "";
            return $"({Mm(points.Min(p => p.X))},{Mm(points.Min(p => p.Y))})..({Mm(points.Max(p => p.X))},{Mm(points.Max(p => p.Y))})";
        }

        private static string Box(BoundingBoxXYZ box) => box == null ? "none" : Pt(box.Min) + ".." + Pt(box.Max);

        /// <summary>Point in mm (0.01 mm).</summary>
        private static string Pt(XYZ p) => p == null ? "" : $"({Mm(p.X)},{Mm(p.Y)},{Mm(p.Z)})";

        /// <summary>Unit direction (1e-4).</summary>
        private static string Dir(XYZ p) => p == null ? "" : $"({Num(p.X, 4)},{Num(p.Y, 4)},{Num(p.Z, 4)})";

        private static string Mm(double feet) => Num(feet * 304.8, 2);

        private static string Cm3(double cubicFeet) => Num(cubicFeet * 28316.846592, 2);

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

        // ---------------------------------------------------------------- tolerant comparison

        private static readonly Regex NumberPattern = new Regex(@"-?\d+(?:\.\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Numbers match within 0.05 (mm, cm3, or 5e-2 of a count) plus 1e-5 relative.</summary>
        private static bool Close(double a, double b) => Math.Abs(a - b) <= 0.05 + 1e-5 * Math.Max(Math.Abs(a), Math.Abs(b));

        private sealed class ParsedToken
        {
            public string Text;
            public string Template;
            public double[] Numbers;
        }

        private static ParsedToken Parse(string token)
        {
            var numbers = new List<double>();
            var template = NumberPattern.Replace(token, m =>
            {
                numbers.Add(double.Parse(m.Value, CultureInfo.InvariantCulture));
                return "#";
            });
            return new ParsedToken { Text = token, Template = template, Numbers = numbers.ToArray() };
        }

        private static bool Close(ParsedToken a, ParsedToken b)
        {
            if (a.Template != b.Template || a.Numbers.Length != b.Numbers.Length)
                return false;
            for (var i = 0; i < a.Numbers.Length; i++)
                if (!Close(a.Numbers[i], b.Numbers[i]))
                    return false;
            return true;
        }

        /// <summary>
        ///     Order-independent multiset match of two token lists: exact matches first, then the rest
        ///     pairwise within tolerance. Returns the tokens left over on each side.
        /// </summary>
        public static void Unmatched(IList<string> a, IList<string> b, out List<string> onlyA, out List<string> onlyB)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var token in b)
                counts[token] = counts.TryGetValue(token, out var n) ? n + 1 : 1;
            var restA = new List<string>();
            foreach (var token in a)
            {
                if (counts.TryGetValue(token, out var n) && n > 0)
                    counts[token] = n - 1;
                else
                    restA.Add(token);
            }

            var restB = new List<string>();
            foreach (var pair in counts)
                for (var i = 0; i < pair.Value; i++)
                    restB.Add(pair.Key);

            onlyA = new List<string>();
            onlyB = new List<string>();
            if (restA.Count == 0 || restB.Count == 0 || restA.Count * (long)restB.Count > 4000000)
            {
                onlyA.AddRange(restA);
                onlyB.AddRange(restB);
            }
            else
            {
                var pool = restB.Select(Parse).GroupBy(p => p.Template).ToDictionary(g => g.Key, g => g.ToList());
                foreach (var token in restA.Select(Parse))
                {
                    if (pool.TryGetValue(token.Template, out var candidates))
                    {
                        var index = candidates.FindIndex(c => Close(token, c));
                        if (index >= 0)
                        {
                            candidates.RemoveAt(index);
                            continue;
                        }
                    }

                    onlyA.Add(token.Text);
                }

                onlyB.AddRange(pool.Values.SelectMany(l => l.Select(p => p.Text)));
            }

            onlyA.Sort(StringComparer.Ordinal);
            onlyB.Sort(StringComparer.Ordinal);
        }

        public static bool SameTokens(IList<string> a, IList<string> b)
        {
            if (a.Count != b.Count)
                return false;
            Unmatched(a, b, out var onlyA, out var onlyB);
            return onlyA.Count == 0 && onlyB.Count == 0;
        }

        /// <summary>
        ///     Human-readable differences between two token lists, e.g.
        ///     "forms (1 each): keep 'Extrusion solid depth=400 ...' vs merge 'Extrusion solid depth=500 ...'".
        /// </summary>
        public static List<string> TokenDifferences(string aspect, IList<string> keep, IList<string> merge, int examples = 3)
        {
            var result = new List<string>();
            Unmatched(keep, merge, out var onlyKeep, out var onlyMerge);
            if (onlyKeep.Count == 0 && onlyMerge.Count == 0)
                return result;
            var counts = keep.Count == merge.Count ? $"{keep.Count} each" : $"{keep.Count} vs {merge.Count}";
            if (onlyKeep.Count == 1 && onlyMerge.Count == 1)
            {
                result.Add($"{aspect} ({counts}): keep '{onlyKeep[0]}' vs merge '{onlyMerge[0]}'");
                return result;
            }

            var line = $"{aspect} ({counts}): {onlyKeep.Count} only on keep, {onlyMerge.Count} only on merge";
            if (onlyKeep.Count > 0)
                line += "; keep e.g. " + string.Join(" | ", onlyKeep.Take(examples).Select(t => "'" + t + "'"));
            if (onlyMerge.Count > 0)
                line += "; merge e.g. " + string.Join(" | ", onlyMerge.Take(examples).Select(t => "'" + t + "'"));
            result.Add(line);
            return result;
        }

        /// <summary>True when the two drawings match within tolerance (hash equality is the fast path).</summary>
        public static bool SameDrawing(Drawing a, Drawing b)
        {
            if (a == null || b == null)
                return false;
            if (a.Hash == b.Hash)
                return true;
            foreach (var aspect in a.Aspects.Keys.Union(b.Aspects.Keys))
                if (!SameTokens(a.Tokens(aspect), b.Tokens(aspect)))
                    return false;
            return true;
        }

        /// <summary>Which aspects of two drawings differ and how (empty when they match within tolerance).</summary>
        public static List<string> DrawingDifferences(Drawing keep, Drawing merge)
        {
            var result = new List<string>();
            if (keep == null || merge == null || keep.Hash == merge.Hash)
                return result;
            foreach (var aspect in keep.Aspects.Keys.Union(merge.Aspects.Keys).OrderBy(k => k, StringComparer.Ordinal))
                result.AddRange(TokenDifferences(aspect, keep.Tokens(aspect), merge.Tokens(aspect)));
            return result;
        }

        /// <summary>A family's drawing: tokens per aspect (lines, regions, texts, forms, nested, imports, other, category).</summary>
        public sealed class Drawing
        {
            public static readonly Drawing Empty = CreateEmpty();

            public SortedDictionary<string, List<string>> Aspects { get; } = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            public DrawingSummary Summary { get; } = new DrawingSummary();
            public string Hash { get; private set; } = "";

            private static Drawing CreateEmpty()
            {
                var drawing = new Drawing();
                drawing.Seal();
                return drawing;
            }

            public void Add(string aspect, string token)
            {
                if (!Aspects.TryGetValue(aspect, out var list))
                    Aspects[aspect] = list = new List<string>();
                list.Add(token);
            }

            public IList<string> Tokens(string aspect) => Aspects.TryGetValue(aspect, out var list) ? list : (IList<string>)new List<string>();

            public void Seal()
            {
                foreach (var list in Aspects.Values)
                    list.Sort(StringComparer.Ordinal);
                Hash = FamilyFingerprint.Hash(Aspects.SelectMany(a => a.Value.Select(t => a.Key + ":" + t)));
            }
        }

        /// <summary>Counts of the drawn elements found in a family (reported to the user).</summary>
        public class DrawingSummary
        {
            private readonly SortedDictionary<string, int> _other = new SortedDictionary<string, int>(StringComparer.Ordinal);

            public int Lines { get; set; }
            public int Regions { get; set; }
            public int Texts { get; set; }
            public int Forms { get; set; }
            public int Nested { get; set; }
            public int Imports { get; set; }
            public int Other { get; set; }

            public void AddOther(string kind)
            {
                Other++;
                _other[kind] = _other.TryGetValue(kind, out var n) ? n + 1 : 1;
            }

            public override string ToString() =>
                $"{Lines} lines, {Regions} regions, {Texts} texts/labels, {Forms} forms, {Nested} nested, {Imports} imports, {Other} other" +
                (_other.Count > 0 ? " (" + string.Join(", ", _other.Select(o => o.Value + " " + o.Key)) + ")" : "");
        }
    }
}
