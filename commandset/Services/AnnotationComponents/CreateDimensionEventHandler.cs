//
//                       RevitAPI-Solutions
// Copyright (c) Duong Tran Quang (DTDucas) (baymax.contact@gmail.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
//

using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents;

/// <summary>
///     Creates linear dimensions. References come from (1) explicit
///     element references in chain order, (2) snapping startPoint/endPoint to
///     nearby detail lines, line end points or detail components, or
///     (3) faces of model elements. Each dimension is isolated in a
///     sub-transaction; a dimension with fewer than 2 references or a 0 value
///     is never created. Coordinates are millimetres.
/// </summary>
public class CreateDimensionEventHandler : JsonParameterEventHandler
{
    private const double ParallelTolerance = 0.01;

    public override string GetName() => "Create Dimension";

    protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
    {
        var doc = uiDoc.Document;
        var dimensions = DocumentationUtils.RequireArray(parameters, "dimensions");

        var results = DocumentationUtils.RunBatch(doc, "MCP: Create Dimensions", dimensions,
            item => Create(uiDoc, (JObject)item));

        var succeeded = results.Count(r => r.Value<bool>("success"));
        return new AIResult<object>
        {
            Success = succeeded > 0,
            Message = $"Created {succeeded} of {results.Count} dimensions.",
            Response = DocumentationUtils.Summarize(results)
        };
    }

    private static object Create(UIDocument uiDoc, JObject item)
    {
        var doc = uiDoc.Document;
        var view = DetailGeometry.ResolveView(uiDoc, item);
        var normal = view.ViewDirection.Normalize();

        var start = ReadOptionalPoint(view, item, "startPoint");
        var end = ReadOptionalPoint(view, item, "endPoint");
        var linePoint = ReadOptionalPoint(view, item, "linePoint");
        if ((start == null) != (end == null))
            throw new ArgumentException("startPoint and endPoint go together.");

        XYZ direction = null;
        if (start != null)
        {
            direction = InPlane(end - start, normal);
            if (direction == null)
                throw new ArgumentException("startPoint and endPoint coincide in the view plane.");
        }

        // Resolve the type first so a bad name fails before anything is created.
        var type = DimensionUtils.ResolveType(doc, DocumentationUtils.ReadId(item, "dimensionStyleId"),
            item.Value<string>("dimensionType"));

        var references = new List<Reference>();
        var explicitRefs = item["references"] as JArray;
        var elementIds = item["elementIds"]?.ToObject<List<long>>() ?? new List<long>();

        if (explicitRefs != null && explicitRefs.Count > 0)
        {
            var targets = explicitRefs.Select((t, i) => ReadTarget(doc, t, i)).ToList();
            direction = direction ?? DeriveDirection(targets, normal);
            references.AddRange(targets.Select(t => t.GetReference(view, direction)));
        }
        else if (elementIds.Count > 0)
        {
            if (direction == null)
                throw new ArgumentException("elementIds need startPoint and endPoint.");
            foreach (var id in elementIds)
            {
                var element = doc.GetElement(id.ToRevitElementId())
                              ?? throw new ArgumentException($"Element {id} not found.");
                references.Add(GetElementReference(element, view, direction)
                               ?? throw new ArgumentException($"No dimensionable reference on element {id}."));
            }
        }
        else
        {
            if (direction == null)
                throw new ArgumentException("Provide references, elementIds or startPoint/endPoint.");
            var tolerance = SnapTolerance(view, item);
            references.Add(FindReferenceAtPoint(doc, view, start, direction, tolerance, "startPoint"));
            references.Add(FindReferenceAtPoint(doc, view, end, direction, tolerance, "endPoint"));
        }

        var refArray = new ReferenceArray();
        var seen = new HashSet<string>();
        foreach (var reference in references)
        {
            if (seen.Add(StableKey(doc, reference)))
                refArray.Append(reference);
        }

        if (refArray.Size < 2)
            throw new ArgumentException(
                "Fewer than 2 distinct references were found; the dimension was not created. " +
                "Reference two different detail lines / end points.");

        var origin = linePoint ?? start
                     ?? throw new ArgumentException("linePoint (or startPoint/endPoint) is required to place the dimension line.");
        var line = Line.CreateBound(origin, origin + direction);

        var dimension = doc.Create.NewDimension(view, line, refArray)
                        ?? throw new InvalidOperationException("Revit did not create the dimension.");
        if (type != null && dimension.GetTypeId() != type.Id)
            dimension.ChangeTypeId(type.Id);
        doc.Regenerate();
        DimensionUtils.RequireMeasurable(dimension);

        var warnings = new List<string>();
        if (DimensionUtils.HasText(item))
        {
            DimensionUtils.ApplyText(dimension, item, warnings);
            doc.Regenerate();
        }

        var result = DimensionUtils.Describe(doc, dimension);
        if (warnings.Count > 0)
            result["warnings"] = new JArray(warnings);
        return result;
    }

    #region Reference targets

    /// <summary>One explicit reference: an element plus which of its references to use.</summary>
    private sealed class Target
    {
        public Element Element;
        public string End;
        public long Id;

        /// <summary>Position used to derive the dimension direction when no span is given.</summary>
        public XYZ Anchor;

        /// <summary>Direction of a straight curve referenced as a whole.</summary>
        public XYZ CurveDirection;

        public Reference GetReference(View view, XYZ direction)
        {
            if (Element is CurveElement curveElement)
            {
                var curve = curveElement.GeometryCurve;
                switch (End)
                {
                    case "start":
                        return curve.GetEndPointReference(0) ?? throw NoReference();
                    case "end":
                        return curve.GetEndPointReference(1) ?? throw NoReference();
                    case "curve":
                        if (!(curve is Line))
                            throw new ArgumentException(
                                $"Element {Id} is not straight; reference its 'start' or 'end' instead.");
                        if (Math.Abs(((Line)curve).Direction.Normalize().DotProduct(direction)) > ParallelTolerance)
                            throw new ArgumentException(
                                $"Line {Id} is not perpendicular to the dimension direction; reference its 'start' or 'end' instead.");
                        return curve.Reference ?? throw NoReference();
                    default:
                        throw new ArgumentException($"end '{End}' does not apply to line {Id}; use curve, start or end.");
                }
            }

            if (Element is FamilyInstance instance && End != "curve")
                return FamilyInstanceReference(instance, End, direction) ?? throw NoReference();

            if (End == "start" || End == "end")
                throw new ArgumentException($"end '{End}' only applies to detail or model lines (element {Id}).");

            if (End != "curve")
                throw new ArgumentException($"end '{End}' only applies to family instances (element {Id}).");

            return GetElementReference(Element, view, direction) ?? throw NoReference();
        }

        private ArgumentException NoReference() =>
            new ArgumentException($"Element {Id} has no '{End}' reference usable for dimensioning.");
    }

    private static Target ReadTarget(Document doc, JToken token, int index)
    {
        var id = DocumentationUtils.ReadId(token, "elementId")
                 ?? throw new ArgumentException($"references[{index}].elementId is required.");
        var element = doc.GetElement(id.ToRevitElementId())
                      ?? throw new ArgumentException($"references[{index}]: element {id} not found.");
        var end = (token.Value<string>("end") ?? "curve").Trim();
        var target = new Target { Element = element, End = end, Id = id };

        if (element is CurveElement curveElement)
        {
            var curve = curveElement.GeometryCurve;
            if (end == "start" || end == "end")
                target.Anchor = curve.GetEndPoint(end == "start" ? 0 : 1);
            else
            {
                target.Anchor = curve.Evaluate(0.5, true);
                if (curve is Line l)
                    target.CurveDirection = l.Direction;
            }
        }
        else if (element.Location is LocationPoint lp)
        {
            target.Anchor = lp.Point;
        }
        else if (element.Location is LocationCurve lc)
        {
            target.Anchor = lc.Curve.Evaluate(0.5, true);
        }

        return target;
    }

    /// <summary>
    ///     Dimension direction from the references alone: perpendicular to the
    ///     first whole line, otherwise from the first to the last anchor.
    /// </summary>
    private static XYZ DeriveDirection(List<Target> targets, XYZ normal)
    {
        var line = targets.FirstOrDefault(t => t.End == "curve" && t.CurveDirection != null);
        if (line != null)
        {
            var across = InPlane(normal.CrossProduct(line.CurveDirection), normal);
            if (across != null)
                return across;
        }

        var first = targets.First().Anchor;
        var last = targets.Last().Anchor;
        var direction = first != null && last != null ? InPlane(last - first, normal) : null;
        return direction ?? throw new ArgumentException(
            "Cannot derive the dimension direction from the references; pass startPoint/endPoint.");
    }

    private static Reference FamilyInstanceReference(FamilyInstance instance, string end, XYZ direction)
    {
        FamilyInstanceReferenceType kind;
        switch (end)
        {
            case "left": kind = FamilyInstanceReferenceType.Left; break;
            case "right": kind = FamilyInstanceReferenceType.Right; break;
            case "front": kind = FamilyInstanceReferenceType.Front; break;
            case "back": kind = FamilyInstanceReferenceType.Back; break;
            case "top": kind = FamilyInstanceReferenceType.Top; break;
            case "bottom": kind = FamilyInstanceReferenceType.Bottom; break;
            case "center":
                return CenterReference(instance, direction);
            default:
                throw new ArgumentException($"end '{end}' does not apply to family instance {instance.Id.GetValue()}.");
        }

        return instance.GetReferences(kind).FirstOrDefault();
    }

    /// <summary>Center (left/right or front/back) reference plane whose normal runs along the dimension.</summary>
    private static Reference CenterReference(FamilyInstance instance, XYZ direction)
    {
        var transform = instance.GetTransform();
        var alongX = Math.Abs(transform.BasisX.Normalize().DotProduct(direction));
        var alongY = Math.Abs(transform.BasisY.Normalize().DotProduct(direction));
        var kind = alongX >= alongY
            ? FamilyInstanceReferenceType.CenterLeftRight
            : FamilyInstanceReferenceType.CenterFrontBack;
        return instance.GetReferences(kind).FirstOrDefault();
    }

    #endregion

    #region Point snapping

    /// <summary>Default snap radius: 2 mm on paper at the view scale, at least 1 mm.</summary>
    private static double SnapTolerance(View view, JObject item)
    {
        var mm = item.Value<double?>("snapToleranceMm");
        if (mm == null || mm <= 0)
        {
            var scale = 1;
            try { scale = Math.Max(1, view.Scale); } catch (Exception) { /* views without scale */ }
            mm = Math.Max(1.0, 2.0 * scale);
        }

        return DocumentationUtils.MmToFeet(mm.Value);
    }

    /// <summary>
    ///     Reference for a point: a straight line perpendicular to the
    ///     dimension (whole-curve reference) wins, then the nearest line end
    ///     point, then a detail component's center plane. Model views fall back
    ///     to faces of the nearest element. Throws when nothing is in range.
    /// </summary>
    private static Reference FindReferenceAtPoint(Document doc, View view, XYZ point, XYZ direction,
        double tolerance, string what)
    {
        Reference bestCurve = null, bestEnd = null;
        double curveDistance = double.MaxValue, endDistance = double.MaxValue;

        var curves = new FilteredElementCollector(doc, view.Id).OfClass(typeof(CurveElement))
            .Cast<CurveElement>();
        foreach (var element in curves)
        {
            Curve curve;
            try { curve = element.GeometryCurve; } catch (Exception) { continue; }
            if (curve == null || !curve.IsBound)
                continue;

            for (var i = 0; i < 2; i++)
            {
                var distance = DetailGeometry.Project(view, curve.GetEndPoint(i)).DistanceTo(point);
                if (distance <= tolerance && distance < endDistance)
                {
                    var reference = curve.GetEndPointReference(i);
                    if (reference == null)
                        continue;
                    endDistance = distance;
                    bestEnd = reference;
                }
            }

            if (curve is Line line && curve.Reference != null
                && Math.Abs(line.Direction.Normalize().DotProduct(direction)) <= ParallelTolerance)
            {
                var a = DetailGeometry.Project(view, line.GetEndPoint(0));
                var b = DetailGeometry.Project(view, line.GetEndPoint(1));
                if (a.IsAlmostEqualTo(b))
                    continue;
                var distance = Line.CreateBound(a, b).Distance(point);
                if (distance <= tolerance && distance < curveDistance)
                {
                    curveDistance = distance;
                    bestCurve = curve.Reference;
                }
            }
        }

        if (bestCurve != null)
            return bestCurve;
        if (bestEnd != null)
            return bestEnd;

        var component = new FilteredElementCollector(doc, view.Id).OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(f => f.Location is LocationPoint)
            .Select(f => new
            {
                Instance = f,
                Distance = DetailGeometry.Project(view, ((LocationPoint)f.Location).Point).DistanceTo(point)
            })
            .Where(x => x.Distance <= tolerance)
            .OrderBy(x => x.Distance)
            .FirstOrDefault();
        var componentRef = component != null ? CenterReference(component.Instance, direction) : null;
        if (componentRef != null)
            return componentRef;

        if (!(view is ViewDrafting))
        {
            var modelRef = NearestModelReference(doc, view, point, direction);
            if (modelRef != null)
                return modelRef;
        }

        throw new ArgumentException(
            $"No detail line, line end point or detail component within {DocumentationUtils.FeetToMm(tolerance)} mm of {what}; " +
            "the dimension was not created. Move the point onto a line perpendicular to the dimension or onto a line end, " +
            "raise snapToleranceMm, or pass explicit references.");
    }

    /// <summary>Legacy model-view fallback: faces of the nearest element with a location (within 5 ft).</summary>
    private static Reference NearestModelReference(Document doc, View view, XYZ point, XYZ direction)
    {
        Element closest = null;
        var minDistance = double.MaxValue;
        foreach (var element in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
        {
            if (element is CurveElement || element.ViewSpecific)
                continue;
            XYZ location;
            if (element.Location is LocationPoint lp)
                location = lp.Point;
            else if (element.Location is LocationCurve lc)
                location = lc.Curve.Project(point)?.XYZPoint;
            else
                continue;
            if (location == null)
                continue;

            var distance = DetailGeometry.Project(view, location).DistanceTo(point);
            if (distance < minDistance)
            {
                minDistance = distance;
                closest = element;
            }
        }

        return closest != null && minDistance < 5.0 ? GetElementReference(closest, view, direction) : null;
    }

    #endregion

    #region Element references

    /// <summary>
    ///     Reference of a model element for a dimension along a direction:
    ///     the planar face most aligned with the direction for walls and family
    ///     instances (center plane for faceless detail components), the line
    ///     itself for straight curves, the element for grids and others.
    /// </summary>
    private static Reference GetElementReference(Element element, View view, XYZ direction)
    {
        if (element is CurveElement curveElement)
            return curveElement.GeometryCurve?.Reference;

        if (element is ReferencePlane plane)
            return plane.GetReference();

        if (element is Wall || element is FamilyInstance)
        {
            var face = BestFaceReference(element, view, direction);
            if (face != null)
                return face;
            if (element is FamilyInstance instance)
                return CenterReference(instance, direction) ?? new Reference(instance);
        }

        return new Reference(element);
    }

    private static Reference BestFaceReference(Element element, View view, XYZ direction)
    {
        var options = new Options { View = view, ComputeReferences = true };
        var geometry = element.get_Geometry(options);
        if (geometry == null)
            return null;

        Reference best = null;
        var bestAlignment = 0.5;
        foreach (var solid in Solids(geometry))
        foreach (Face face in solid.Faces)
        {
            if (!(face is PlanarFace planar) || face.Reference == null)
                continue;
            var alignment = Math.Abs(planar.FaceNormal.DotProduct(direction));
            if (alignment > bestAlignment)
            {
                bestAlignment = alignment;
                best = face.Reference;
            }
        }

        return best;
    }

    private static IEnumerable<Solid> Solids(GeometryElement geometry)
    {
        foreach (var obj in geometry)
        {
            if (obj is Solid solid && solid.Faces.Size > 0)
                yield return solid;
            else if (obj is GeometryInstance instance)
            {
                foreach (var sub in instance.GetInstanceGeometry())
                {
                    if (sub is Solid subSolid && subSolid.Faces.Size > 0)
                        yield return subSolid;
                }
            }
        }
    }

    #endregion

    #region Helpers

    private static XYZ ReadOptionalPoint(View view, JObject item, string name)
    {
        var token = item[name];
        if (token == null || token.Type == JTokenType.Null)
            return null;
        return DetailGeometry.ReadPoint(view, token, name);
    }

    /// <summary>Unit vector of v projected into the view plane, or null when it vanishes.</summary>
    private static XYZ InPlane(XYZ vector, XYZ normal)
    {
        var projected = vector - normal.Multiply(normal.DotProduct(vector));
        return projected.GetLength() < 1e-9 ? null : projected.Normalize();
    }

    private static string StableKey(Document doc, Reference reference)
    {
        try
        {
            return reference.ConvertToStableRepresentation(doc);
        }
        catch (Exception)
        {
            return Guid.NewGuid().ToString();
        }
    }

    #endregion
}
