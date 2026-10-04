using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Edits view-specific 2D elements: move, copy, rotate, delete, change
    ///     text, line style, line geometry, type or parameters. All operations run
    ///     in one undoable transaction; each operation is isolated so a failure
    ///     does not discard the others. Coordinates are millimetres.
    /// </summary>
    public class ModifyAnnotationsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Modify Annotations";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var operations = DocumentationUtils.RequireArray(parameters, "operations");

            var results = DocumentationUtils.RunBatch(doc, "MCP: Modify Annotations", operations,
                op => Apply(doc, (JObject)op));

            return Ok($"Applied {results.Count(r => r.Value<bool>("success"))} of {results.Count} operations.",
                DocumentationUtils.Summarize(results));
        }

        private static object Apply(Document doc, JObject op)
        {
            var action = op.Value<string>("action") ?? throw new ArgumentException("'action' is required.");
            var elements = ReadElements(doc, op);
            var ids = elements.Select(e => e.Id).ToList();

            switch (action)
            {
                case "move":
                    ElementTransformUtils.MoveElements(doc, ids, ReadVector(op, "delta"));
                    return new { action, elementIds = ids.Select(i => i.GetValue()) };

                case "copy":
                    var copies = ElementTransformUtils.CopyElements(doc, ids, ReadVector(op, "delta"));
                    return new { action, elementIds = ids.Select(i => i.GetValue()), newIds = copies.Select(i => i.GetValue()) };

                case "rotate":
                {
                    var view = OwnerView(doc, elements[0]);
                    var center = DetailGeometry.ReadPoint(view, op["center"], "center");
                    var angle = (op.Value<double?>("angleDeg") ?? throw new ArgumentException("'angleDeg' is required."))
                                * Math.PI / 180;
                    var axis = Line.CreateBound(center, center + view.ViewDirection);
                    ElementTransformUtils.RotateElements(doc, ids, axis, angle);
                    return new { action, elementIds = ids.Select(i => i.GetValue()) };
                }

                case "delete":
                    var deleted = doc.Delete(ids);
                    return new { action, deletedIds = deleted.Select(i => i.GetValue()) };

                case "setText":
                {
                    var text = op.Value<string>("text") ?? throw new ArgumentException("'text' is required.");
                    foreach (var e in elements)
                        Require<TextNote>(e, action).Text = text;
                    return new { action, elementIds = ids.Select(i => i.GetValue()) };
                }

                case "setLineStyle":
                {
                    var style = DetailGeometry.ResolveLineStyle(doc, op.Value<string>("lineStyle"))
                                ?? throw new ArgumentException("'lineStyle' is required.");
                    foreach (var e in elements)
                        Require<CurveElement>(e, action).LineStyle = style;
                    return new { action, elementIds = ids.Select(i => i.GetValue()), lineStyle = style.Name };
                }

                case "setLine":
                {
                    if (elements.Count != 1)
                        throw new ArgumentException("setLine takes exactly one detail line.");
                    var curve = Require<DetailCurve>(elements[0], action);
                    if (!(curve.GeometryCurve is Line))
                        throw new ArgumentException($"Element {curve.Id.GetValue()} is not a straight line.");
                    var view = OwnerView(doc, curve);
                    var line = Line.CreateBound(DetailGeometry.ReadPoint(view, op["start"], "start"),
                        DetailGeometry.ReadPoint(view, op["end"], "end"));
                    curve.SetGeometryCurve(line, true);
                    return new { action, elementIds = ids.Select(i => i.GetValue()) };
                }

                case "setType":
                {
                    var changed = new List<long>();
                    foreach (var e in elements)
                    {
                        var typeId = ResolveType(doc, e, op);
                        e.ChangeTypeId(typeId);
                        changed.Add(e.Id.GetValue());
                    }

                    return new { action, elementIds = changed };
                }

                case "setParameters":
                {
                    if (!(op["parameters"] is JObject values) || !values.HasValues)
                        throw new ArgumentException("'parameters' must be a non-empty object.");
                    var errors = new List<string>();
                    foreach (var e in elements)
                    foreach (var property in values.Properties())
                    {
                        var error = DocumentationUtils.SetParameterValue(e, property.Name, property.Value);
                        if (error != null)
                            errors.Add($"{e.Id.GetValue()}: {error}");
                    }

                    if (errors.Count > 0)
                        throw new ArgumentException(string.Join(" ", errors));
                    return new { action, elementIds = ids.Select(i => i.GetValue()) };
                }

                case "addLeaders":
                case "setLeaders":
                case "removeLeaders":
                {
                    // Text note leaders (see TextNoteStyling); other element types are rejected.
                    var notes = elements.Select(e => TextNoteStyling.RequireTextNote(e, action)).ToList();
                    if (action != "removeLeaders" && !(op["leaders"] is JArray leaders && leaders.Count > 0))
                        throw new ArgumentException($"{action} needs a non-empty 'leaders' array.");
                    foreach (var note in notes)
                    {
                        if (action != "addLeaders")
                            note.RemoveLeaders();
                        if (action == "removeLeaders")
                            continue;
                        TextNoteStyling.ApplyAttachments(note, op);
                        TextNoteStyling.AddLeaders(doc, note, OwnerView(doc, note), op["leaders"]);
                    }

                    return new
                    {
                        action,
                        elementIds = ids.Select(i => i.GetValue()),
                        leaderCounts = notes.Select(n => n.LeaderCount)
                    };
                }

                case "setDimensionText":
                {
                    if (!DimensionUtils.HasText(op))
                        throw new ArgumentException("setDimensionText needs 'text' or 'segments'.");
                    var warnings = new List<string>();
                    var applied = 0;
                    foreach (var e in elements)
                    {
                        var itemWarnings = new List<string>();
                        applied += DimensionUtils.ApplyText(Require<Dimension>(e, action), op, itemWarnings);
                        warnings.AddRange(itemWarnings.Select(w => $"{e.Id.GetValue()}: {w}"));
                    }
                    if (applied == 0)
                        throw new ArgumentException("No dimension text was applied. " + string.Join(" ", warnings));
                    doc.Regenerate();
                    return new { action, elementIds = ids.Select(i => i.GetValue()), applied, warnings,
                        dimensions = elements.Select(e => DimensionUtils.Describe(doc, (Dimension)e)) };
                }

                case "mirror":
                {
                    var view = OwnerView(doc, elements[0]);
                    var axis = op["axis"] ?? throw new ArgumentException("'axis' {start, end} is required.");
                    var a = DetailGeometry.ReadPoint(view, axis["start"], "axis.start");
                    var b = DetailGeometry.ReadPoint(view, axis["end"], "axis.end");
                    if (a.IsAlmostEqualTo(b))
                        throw new ArgumentException("'axis' start and end coincide.");
                    // The mirror plane contains the axis and the view direction.
                    var normal = (b - a).CrossProduct(view.ViewDirection).Normalize();
                    var copy = op.Value<bool?>("copy") ?? false;
                    var mirrored = ElementTransformUtils.MirrorElements(doc, ids, Plane.CreateByNormalAndOrigin(normal, a), copy);
                    return new { action, elementIds = ids.Select(i => i.GetValue()), newIds = copy ? mirrored?.Select(i => i.GetValue()) : null };
                }

                case "flip":
                    return new { action, flipped = elements.Select(e => new { elementId = e.Id.GetValue(), method = Flip(doc, e) }).ToList() };

                default:
                    throw new ArgumentException(
                        $"Unknown action '{action}'. Use move, copy, rotate, delete, setText, setLineStyle, setLine, setType, setParameters, addLeaders, setLeaders, removeLeaders, setDimensionText, mirror or flip.");
            }
        }

        private static List<Element> ReadElements(Document doc, JObject op)
        {
            var ids = op["elementIds"]?.ToObject<List<long>>() ?? new List<long>();
            if (ids.Count == 0)
                throw new ArgumentException("'elementIds' must list at least one element.");

            var elements = new List<Element>();
            var missing = new List<long>();
            foreach (var id in ids.Distinct())
            {
                var element = doc.GetElement(id.ToRevitElementId());
                if (element == null || element is ElementType)
                    missing.Add(id);
                else
                    elements.Add(element);
            }

            if (missing.Count > 0)
                throw new ArgumentException($"Elements not found: {string.Join(", ", missing)}.");
            return elements;
        }

        private static XYZ ReadVector(JObject op, string name)
        {
            var vector = DocumentationUtils.ReadPointMm(op[name])
                         ?? throw new ArgumentException($"'{name}' must be a vector {{x, y, z?}} in mm.");
            if (vector.IsZeroLength())
                throw new ArgumentException($"'{name}' is zero.");
            return vector;
        }

        private static View OwnerView(Document doc, Element element)
        {
            return doc.GetElement(element.OwnerViewId) as View
                   ?? throw new ArgumentException($"Element {element.Id.GetValue()} is not view-specific.");
        }

        private static T Require<T>(Element element, string action) where T : Element
        {
            return element as T
                   ?? throw new ArgumentException(
                       $"{action} does not apply to element {element.Id.GetValue()} ({element.Category?.Name ?? element.GetType().Name}).");
        }

        private static ElementId ResolveType(Document doc, Element element, JObject op)
        {
            var valid = element.GetValidTypes();
            var typeId = DocumentationUtils.ReadId(op, "typeId");
            if (typeId != null)
            {
                var id = typeId.Value.ToRevitElementId();
                if (!valid.Contains(id))
                    throw new ArgumentException($"Type {typeId} is not valid for element {element.Id.GetValue()}.");
                return id;
            }

            var name = op.Value<string>("typeName");
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("setType needs 'typeId' or 'typeName'.");
            var match = valid.FirstOrDefault(id =>
                string.Equals(doc.GetElement(id)?.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match == null)
                throw new ArgumentException($"Type '{name}' not found for element {element.Id.GetValue()}. Valid: " +
                                            string.Join(", ", valid.Select(id => doc.GetElement(id)?.Name).Distinct().Take(30)) + ".");
            return match;
        }

        /// <summary>
        ///     Flips a detail family instance in place: line-based items reverse their location curve
        ///     (swapping the side a break line masks), point-based items flip hand/facing or, failing
        ///     that, are mirrored about their own vertical axis. Returns the method used.
        /// </summary>
        private static string Flip(Document doc, Element element)
        {
            var instance = Require<FamilyInstance>(element, "flip");
            if (instance.Location is LocationCurve location)
            {
                location.Curve = location.Curve.CreateReversed();
                return "reverseCurve";
            }

            if (instance.CanFlipHand && instance.flipHand())
                return "flipHand";
            if (instance.CanFlipFacing && instance.flipFacing())
                return "flipFacing";

            var view = OwnerView(doc, instance);
            var origin = (instance.Location as LocationPoint)?.Point
                         ?? throw new ArgumentException($"Element {instance.Id.GetValue()} has no location to flip about.");
            var hand = instance.HandOrientation;
            var normal = hand == null || hand.IsZeroLength() ? view.RightDirection : hand.Normalize();
            ElementTransformUtils.MirrorElements(doc, new List<ElementId> { instance.Id },
                Plane.CreateByNormalAndOrigin(normal, origin), false);
            return "mirror";
        }
    }
}
