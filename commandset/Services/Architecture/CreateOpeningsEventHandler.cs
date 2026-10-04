using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services.Foundations;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Architecture
{
    /// <summary>
    ///     create_openings: shaft openings between levels, rectangular wall openings and
    ///     polygon floor openings. One transaction per call (chunk), one sub-transaction per item.
    ///     All wire lengths are millimetres.
    /// </summary>
    public class CreateOpeningsEventHandler : JsonParameterEventHandler
    {
        private const double Tolerance = 1.0 / 304.8;

        public override string GetName() => "Create Openings";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "openings");
            var ctx = new FoundationModelUtils.Context(doc);
            var results = DocumentationUtils.RunBatch(doc, "MCP: Create Openings", items, token =>
            {
                if (!(token is JObject item))
                    throw new ArgumentException("Each opening must be an object.");
                var kind = item.Value<string>("kind");
                JObject result;
                switch ((kind ?? "").ToLowerInvariant())
                {
                    case "shaft":
                        result = CreateShaft(ctx, item);
                        break;
                    case "wall":
                        result = CreateWallOpening(ctx, item);
                        break;
                    case "floor":
                        result = CreateFloorOpening(ctx, item);
                        break;
                    default:
                        throw new ArgumentException($"Unknown kind '{kind}' (shaft, wall, floor).");
                }
                result.AddFirst(new JProperty("kind", kind));
                return result;
            });
            return Ok($"Created {results.Count(r => r.Value<bool>("success"))} of {results.Count} openings.",
                DocumentationUtils.Summarize(results));
        }

        private static CurveArray ToCurveArray(CurveLoop loop)
        {
            var curves = new CurveArray();
            foreach (var curve in loop)
                curves.Append(curve);
            return curves;
        }

        // ---------------------------------------------------------------- shaft

        private static JObject CreateShaft(FoundationModelUtils.Context ctx, JObject item)
        {
            var doc = ctx.Doc;
            var warnings = new List<string>();
            var baseLevel = FoundationModelUtils.ResolveLevel(ctx, item["baseLevel"], warnings, out var baseExtra, "baseLevel");
            var topLevel = FoundationModelUtils.ResolveLevel(ctx, item["topLevel"], warnings, out var topExtra, "topLevel");
            var baseOffset = FoundationModelUtils.ToFeet(item.Value<double?>("baseOffset") ?? 0) + baseExtra;
            var topOffset = FoundationModelUtils.ToFeet(item.Value<double?>("topOffset") ?? 0) + topExtra;
            var bottom = baseLevel.Elevation + baseOffset;
            var top = topLevel.Elevation + topOffset;
            if (top - bottom < 10 * Tolerance)
                throw new ArgumentException(
                    $"Shaft top ({FoundationModelUtils.ToMm(top)} mm) must be above its bottom ({FoundationModelUtils.ToMm(bottom)} mm).");
            if (topLevel.Elevation < baseLevel.Elevation - Tolerance)
                throw new ArgumentException($"topLevel '{topLevel.Name}' is below baseLevel '{baseLevel.Name}'.");

            var loop = FoundationModelUtils.LoopFromPoints(item["boundary"] as JArray, baseLevel.Elevation, "boundary");
            var opening = doc.Create.NewOpening(baseLevel, topLevel, ToCurveArray(loop))
                          ?? throw new InvalidOperationException("Revit did not create the shaft opening.");

            SetLength(opening, baseOffset, warnings, "base offset", BuiltInParameter.WALL_BASE_OFFSET, "Base Offset");
            SetLength(opening, topOffset, warnings, "top offset", BuiltInParameter.WALL_TOP_OFFSET, "Top Offset");
            FoundationModelUtils.SetMark(ctx, opening, item, warnings);
            doc.Regenerate();

            var result = new JObject
            {
                ["id"] = opening.Id.GetValue(),
                ["baseLevelId"] = baseLevel.Id.GetValue(),
                ["baseLevelName"] = baseLevel.Name,
                ["topLevelId"] = topLevel.Id.GetValue(),
                ["topLevelName"] = topLevel.Name,
                ["bottomElevationMm"] = FoundationModelUtils.ToMm(bottom),
                ["topElevationMm"] = FoundationModelUtils.ToMm(top)
            };
            FoundationModelUtils.AddWarnings(result, warnings);
            return result;
        }

        /// <summary>Sets a length parameter by built-in id, falling back to the English name.</summary>
        private static void SetLength(Element element, double feet, List<string> warnings, string what,
            BuiltInParameter builtIn, string name)
        {
            var parameter = element.get_Parameter(builtIn);
            if (parameter == null || parameter.StorageType != StorageType.Double)
                parameter = element.LookupParameter(name);
            if (parameter != null && !parameter.IsReadOnly && parameter.StorageType == StorageType.Double)
            {
                parameter.Set(feet);
                return;
            }
            if (Math.Abs(feet) > 1e-9)
                warnings.Add($"Could not set the {what} ({FoundationModelUtils.ToMm(feet)} mm).");
        }

        // ---------------------------------------------------------------- wall

        private static JObject CreateWallOpening(FoundationModelUtils.Context ctx, JObject item)
        {
            var doc = ctx.Doc;
            var warnings = new List<string>();
            var width = FoundationModelUtils.ToFeet(item.Value<double?>("width") ?? throw new ArgumentException("'width' is required (mm)."));
            var height = FoundationModelUtils.ToFeet(item.Value<double?>("height") ?? throw new ArgumentException("'height' is required (mm)."));
            if (width <= 0 || height <= 0)
                throw new ArgumentException("'width' and 'height' must be positive (mm).");
            var sill = FoundationModelUtils.ToFeet(item.Value<double?>("sillHeight") ?? 0);

            Level level = null;
            double levelExtra = 0;
            if (item["level"] != null && item["level"].Type != JTokenType.Null)
                level = FoundationModelUtils.ResolveLevel(ctx, item["level"], warnings, out levelExtra);
            var point = item["point"] is JObject p && p.HasValues ? FoundationModelUtils.ReadXY(p, 0, "point") : null;

            Wall wall;
            var wallId = DocumentationUtils.ReadId(item, "wallId");
            if (wallId != null)
            {
                wall = doc.GetElement(wallId.Value.ToRevitElementId()) as Wall
                       ?? throw new ArgumentException($"wallId {wallId} is not a wall.");
            }
            else
            {
                if (level == null || point == null)
                    throw new ArgumentException("Give wallId, or point and level to find the host wall.");
                var radius = FoundationModelUtils.ToFeet(item.Value<double?>("searchRadius") ?? 1000);
                var midHeight = level.Elevation + levelExtra + sill + height / 2;
                wall = FindWall(doc, point, midHeight, radius, level)
                       ?? throw new ArgumentException(
                           $"No straight wall within {FoundationModelUtils.ToMm(radius)} mm of ({FoundationModelUtils.ToMm(point.X)}, {FoundationModelUtils.ToMm(point.Y)}) spans the opening height on '{level.Name}'.");
            }

            var line = (wall.Location as LocationCurve)?.Curve as Line
                       ?? throw new ArgumentException($"Wall {wall.Id.GetValue()} is not a straight wall; only straight walls are supported.");
            var start = line.GetEndPoint(0);
            var direction = line.Direction;
            var length = line.Length;

            double along;
            if (point != null)
            {
                var flat = new XYZ(point.X, point.Y, start.Z);
                along = (flat - start).DotProduct(direction);
                var distance = (start + direction * along).DistanceTo(flat);
                if (distance > wall.Width / 2 + Tolerance)
                    warnings.Add($"point is {FoundationModelUtils.ToMm(distance)} mm from the wall line; projected onto it.");
            }
            else
            {
                along = length / 2;
            }
            if (along - width / 2 < -Tolerance || along + width / 2 > length + Tolerance)
                warnings.Add("The opening extends past the wall ends.");

            var wallBase = WallBaseElevation(doc, wall);
            double bottom;
            if (level != null)
            {
                bottom = level.Elevation + levelExtra + sill;
            }
            else
            {
                bottom = wallBase + sill;
            }
            var wallTop = wallBase + (wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0);
            if (bottom < wallBase - Tolerance)
                warnings.Add("The opening starts below the wall base.");
            if (wallTop > wallBase && bottom + height > wallTop + Tolerance)
                warnings.Add("The opening extends above the wall top.");

            // NewOpening takes two opposite corners of the rectangle in the wall plane (model coordinates).
            var centre = start + direction * along;
            var p1 = new XYZ(centre.X, centre.Y, bottom) - direction * (width / 2);
            var p2 = new XYZ(centre.X, centre.Y, bottom + height) + direction * (width / 2);
            var opening = doc.Create.NewOpening(wall, p1, p2)
                          ?? throw new InvalidOperationException("Revit did not create the wall opening.");
            FoundationModelUtils.SetMark(ctx, opening, item, warnings);
            doc.Regenerate();

            var result = new JObject
            {
                ["id"] = opening.Id.GetValue(),
                ["hostId"] = wall.Id.GetValue(),
                ["centre"] = new JObject { ["x"] = FoundationModelUtils.ToMm(centre.X), ["y"] = FoundationModelUtils.ToMm(centre.Y) },
                ["bottomElevationMm"] = FoundationModelUtils.ToMm(bottom),
                ["topElevationMm"] = FoundationModelUtils.ToMm(bottom + height),
                ["sillAboveWallBaseMm"] = FoundationModelUtils.ToMm(bottom - wallBase)
            };
            FoundationModelUtils.AddWarnings(result, warnings);
            return result;
        }

        private static double WallBaseElevation(Document doc, Wall wall)
        {
            var baseLevel = doc.GetElement(wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() ?? wall.LevelId) as Level
                            ?? doc.GetElement(wall.LevelId) as Level;
            var offset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;
            return (baseLevel?.Elevation ?? 0) + offset;
        }

        /// <summary>
        ///     The straight wall nearest the plan point whose vertical extent contains the given elevation;
        ///     walls based on <paramref name="level" /> win ties within 1 mm.
        /// </summary>
        private static Wall FindWall(Document doc, XYZ point, double elevation, double radius, Level level)
        {
            Wall best = null;
            var bestDistance = double.MaxValue;
            foreach (var wall in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
            {
                if (!((wall.Location as LocationCurve)?.Curve is Line line)) continue;
                var box = wall.get_BoundingBox(null);
                if (box == null || elevation < box.Min.Z - Tolerance || elevation > box.Max.Z + Tolerance) continue;

                var start = line.GetEndPoint(0);
                var end = line.GetEndPoint(1);
                var a = new UV(start.X, start.Y);
                var b = new UV(end.X, end.Y);
                var ab = b - a;
                var lengthSquared = ab.DotProduct(ab);
                var t = lengthSquared < 1e-12 ? 0 : Math.Max(0, Math.Min(1, (new UV(point.X, point.Y) - a).DotProduct(ab) / lengthSquared));
                var distance = (a + ab * t).DistanceTo(new UV(point.X, point.Y));
                if (distance > radius) continue;

                var onLevel = wall.LevelId == level.Id;
                var bestOnLevel = best != null && best.LevelId == level.Id;
                if (best == null || distance < bestDistance - Tolerance ||
                    (Math.Abs(distance - bestDistance) <= Tolerance && onLevel && !bestOnLevel))
                {
                    best = wall;
                    bestDistance = distance;
                }
            }
            return best;
        }

        // ---------------------------------------------------------------- floor

        private static JObject CreateFloorOpening(FoundationModelUtils.Context ctx, JObject item)
        {
            var doc = ctx.Doc;
            var warnings = new List<string>();
            var boundary = item["boundary"] as JArray;

            Floor floor;
            var floorId = DocumentationUtils.ReadId(item, "floorId");
            double z;
            if (floorId != null)
            {
                floor = doc.GetElement(floorId.Value.ToRevitElementId()) as Floor
                        ?? throw new ArgumentException($"floorId {floorId} is not a floor or slab.");
                z = (doc.GetElement(floor.LevelId) as Level)?.Elevation ?? 0;
            }
            else
            {
                var level = FoundationModelUtils.ResolveLevel(ctx, item["level"], warnings, out _);
                z = level.Elevation;
                XYZ point;
                if (item["point"] is JObject p && p.HasValues)
                {
                    point = FoundationModelUtils.ReadXY(p, z, "point");
                }
                else
                {
                    var polygon = FoundationModelUtils.Polygon2D(FoundationModelUtils.LoopFromPoints(boundary, z, "boundary"));
                    var centroid = FoundationModelUtils.Centroid(polygon);
                    point = new XYZ(centroid.U, centroid.V, z);
                }
                floor = FindFloor(doc, level, point, warnings)
                        ?? throw new ArgumentException(
                            $"No floor on '{level.Name}' at ({FoundationModelUtils.ToMm(point.X)}, {FoundationModelUtils.ToMm(point.Y)}).");
            }

            var loop = FoundationModelUtils.LoopFromPoints(boundary, z, "boundary");
            var perpendicular = item.Value<bool?>("perpendicular") ?? true;
            var opening = doc.Create.NewOpening(floor, ToCurveArray(loop), perpendicular)
                          ?? throw new InvalidOperationException("Revit did not create the floor opening.");
            FoundationModelUtils.SetMark(ctx, opening, item, warnings);
            doc.Regenerate();

            var result = new JObject
            {
                ["id"] = opening.Id.GetValue(),
                ["hostId"] = floor.Id.GetValue(),
                ["hostName"] = floor.Name,
                ["hostAreaM2"] = FoundationModelUtils.AreaM2(floor)
            };
            FoundationModelUtils.AddWarnings(result, warnings);
            return result;
        }

        /// <summary>Floors on the level whose top face contains the plan point (topmost first).</summary>
        private static Floor FindFloor(Document doc, Level level, XYZ point, List<string> warnings)
        {
            var hits = new List<Floor>();
            foreach (var floor in new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>())
            {
                if (floor.LevelId != level.Id) continue;
                var box = floor.get_BoundingBox(null);
                if (box == null || point.X < box.Min.X - Tolerance || point.X > box.Max.X + Tolerance ||
                    point.Y < box.Min.Y - Tolerance || point.Y > box.Max.Y + Tolerance) continue;
                var probe = Line.CreateBound(new XYZ(point.X, point.Y, box.Min.Z - 1), new XYZ(point.X, point.Y, box.Max.Z + 1));
                if (TopFaceContains(floor, probe))
                    hits.Add(floor);
            }
            if (hits.Count == 0) return null;
            var ordered = hits.OrderByDescending(f => f.get_BoundingBox(null).Max.Z).ToList();
            if (ordered.Count > 1)
                warnings.Add($"{ordered.Count} floors on '{level.Name}' contain the point; used the topmost ({ordered[0].Id.GetValue()}).");
            return ordered[0];
        }

        /// <summary>True when the vertical probe line pierces one of the floor's top faces (works for sloped slabs).</summary>
        private static bool TopFaceContains(Floor floor, Line probe)
        {
            foreach (var reference in HostObjectUtils.GetTopFaces(floor))
            {
                if (!(floor.GetGeometryObjectFromReference(reference) is Face face)) continue;
                if (face.Intersect(probe) == SetComparisonResult.Overlap)
                    return true;
            }
            return false;
        }
    }
}
