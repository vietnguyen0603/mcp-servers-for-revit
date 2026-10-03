using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Creates spot elevations or spot coordinates on element faces. In plan
    ///     and 3D views the highest upward-facing planar face is used; in
    ///     sections and elevations the edge-on face nearest the requested point
    ///     is used. Points are model millimetres.
    /// </summary>
    public class CreateSpotElevationsEventHandler : JsonParameterEventHandler
    {
        /// <summary>Default leader length on paper, in millimetres.</summary>
        private const double DefaultLeaderPaperMm = 8;

        public override string GetName() => "Create Spot Elevations";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var view = DocumentationUtils.GetElement<View>(doc, DocumentationUtils.ReadId(parameters, "viewId"))
                       ?? uiDoc.ActiveView;
            if (view.IsTemplate || view is ViewSheet || view is ViewSchedule || view is ViewDrafting)
                return Fail($"View '{view.Name}' cannot host spot dimensions.");

            var kind = (parameters.Value<string>("kind") ?? "Elevation").Trim();
            var isCoordinate = string.Equals(kind, "Coordinate", StringComparison.OrdinalIgnoreCase);
            if (!isCoordinate && !string.Equals(kind, "Elevation", StringComparison.OrdinalIgnoreCase))
                return Fail($"Invalid kind '{kind}'. Use Elevation or Coordinate.");

            SpotDimensionType spotType = null;
            var spotTypeId = DocumentationUtils.ReadId(parameters, "spotTypeId");
            if (spotTypeId != null)
            {
                spotType = DocumentationUtils.GetElement<SpotDimensionType>(doc, spotTypeId);
                if (spotType == null)
                    return Fail($"spotTypeId {spotTypeId} is not a spot dimension type.");
            }

            var items = DocumentationUtils.RequireArray(parameters, "targets");
            var results = DocumentationUtils.RunBatch(doc, isCoordinate ? "MCP: Create Spot Coordinates" : "MCP: Create Spot Elevations",
                items, item => CreateSpot(doc, view, (JObject)item, isCoordinate, spotType));

            return Ok($"Created {results.Count(r => r.Value<bool>("success"))} of {results.Count} spot {(isCoordinate ? "coordinates" : "elevations")}.",
                DocumentationUtils.Summarize(results));
        }

        private static object CreateSpot(Document doc, View view, JObject item, bool isCoordinate, SpotDimensionType spotType)
        {
            var elementId = DocumentationUtils.ReadId(item, "elementId")
                            ?? throw new ArgumentException("'elementId' is required.");
            var element = doc.GetElement(elementId.ToRevitElementId())
                          ?? throw new ArgumentException($"Element {elementId} not found.");

            var requested = DocumentationUtils.ReadPointMm(item["point"]);
            var hint = requested ?? LocationHint(element, view);
            var (reference, origin) = FindFace(element, view, hint, requested != null)
                                      ?? throw new InvalidOperationException("No suitable face reference found on the element in this view.");

            var hasLeader = item.Value<bool?>("hasLeader") ?? false;
            var leaderLength = DocumentationUtils.MmToFeet(DefaultLeaderPaperMm * view.Scale);
            var diagonal = (view.RightDirection + view.UpDirection).Normalize();
            var bend = DocumentationUtils.ReadPointMm(item["bend"]) ?? origin + diagonal * (leaderLength / 2);
            var end = DocumentationUtils.ReadPointMm(item["leaderEnd"]) ?? origin + diagonal * leaderLength;

            var spot = isCoordinate
                ? doc.Create.NewSpotCoordinate(view, reference, origin, bend, end, origin, hasLeader)
                : doc.Create.NewSpotElevation(view, reference, origin, bend, end, origin, hasLeader);
            if (spot == null)
                throw new InvalidOperationException("Revit did not create the spot dimension.");

            if (spotType != null)
                spot.ChangeTypeId(spotType.Id);

            return new
            {
                elementId,
                spotId = spot.Id.GetValue(),
                origin = DocumentationUtils.PointToMm(origin)
            };
        }

        private static XYZ LocationHint(Element element, View view)
        {
            switch (element.Location)
            {
                case LocationPoint point:
                    return point.Point;
                case LocationCurve curve:
                    return curve.Curve.Evaluate(0.5, true);
            }

            var box = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            return box == null ? null : (box.Min + box.Max) / 2;
        }

        /// <summary>
        ///     Picks a planar face and the point on it where the spot is placed.
        ///     Plan/3D: highest face with normal +Z (containing the hint when it
        ///     projects onto it). Section/elevation: the face seen edge-on that is
        ///     nearest the hint, or the highest top face when no point was given.
        /// </summary>
        private static (Reference reference, XYZ origin)? FindFace(Element element, View view, XYZ hint, bool pointGiven)
        {
            var faces = PlanarFaces(element, view);
            if (faces.Count == 0)
                return null;

            var isPlan = view is ViewPlan || view is View3D;
            var topFaces = faces.Where(f => f.FaceNormal.Z > 0.99).ToList();

            if (!isPlan && pointGiven && hint != null)
            {
                var viewDirection = view.ViewDirection.Normalize();
                var best = faces
                    .Where(f => Math.Abs(f.FaceNormal.DotProduct(viewDirection)) < 0.01)
                    .Select(f => new { face = f, projection = f.Project(hint) })
                    .Where(p => p.projection != null)
                    .OrderBy(p => p.projection.Distance)
                    .FirstOrDefault();
                if (best != null)
                    return (best.face.Reference, best.projection.XYZPoint);
            }

            if (topFaces.Count == 0)
                return null;

            if (hint != null)
            {
                var containing = topFaces
                    .Select(f => new { face = f, projection = f.Project(hint) })
                    .Where(p => p.projection != null)
                    .OrderByDescending(p => p.projection.XYZPoint.Z)
                    .FirstOrDefault();
                if (containing != null)
                    return (containing.face.Reference, containing.projection.XYZPoint);
            }

            var highest = topFaces.OrderByDescending(f => f.Origin.Z).First();
            return (highest.Reference, FaceCenter(highest));
        }

        private static XYZ FaceCenter(Face face)
        {
            var box = face.GetBoundingBox();
            return face.Evaluate((box.Min + box.Max) / 2);
        }

        private static List<PlanarFace> PlanarFaces(Element element, View view)
        {
            var options = new Options { ComputeReferences = true, View = view };
            var faces = new List<PlanarFace>();
            CollectFaces(element.get_Geometry(options), faces);
            return faces;
        }

        private static void CollectFaces(GeometryElement geometry, List<PlanarFace> faces)
        {
            if (geometry == null)
                return;

            foreach (var obj in geometry)
            {
                switch (obj)
                {
                    case Solid solid when solid.Faces.Size > 0:
                        foreach (Face face in solid.Faces)
                        {
                            if (face is PlanarFace planar && planar.Reference != null)
                                faces.Add(planar);
                        }

                        break;
                    case GeometryInstance instance:
                        CollectFaces(instance.GetInstanceGeometry(), faces);
                        break;
                }
            }
        }
    }
}
