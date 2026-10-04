using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Tags elements of any taggable category in one view. Targets are the
    ///     given element ids, or every element of the given categories visible
    ///     in the view. Rooms receive room tags; everything else receives an
    ///     independent tag of the requested type or the category default.
    ///     Optional placement: offset perpendicular to linear elements
    ///     (offsetAlongNormalMm), orientation "Model" (aligned with the element,
    ///     Revit 2022+) and avoidOverlaps (shift colliding tags, report the rest).
    /// </summary>
    public class TagElementsEventHandler : JsonParameterEventHandler
    {
        private const int DefaultMaxTags = 500;
        private const int MaxReportedOverlaps = 50;
        private const double OverlapToleranceFeet = 0.5 / DocumentationUtils.MmPerFoot;

        public override string GetName() => "Tag Elements";

        private class Rect
        {
            public long Id;
            public double MinU, MinV, MaxU, MaxV;

            public bool Overlaps(Rect other) =>
                MinU < other.MaxU - OverlapToleranceFeet && other.MinU < MaxU - OverlapToleranceFeet &&
                MinV < other.MaxV - OverlapToleranceFeet && other.MinV < MaxV - OverlapToleranceFeet;
        }

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var view = DocumentationUtils.GetElement<View>(doc, DocumentationUtils.ReadId(parameters, "viewId"))
                       ?? uiDoc.ActiveView;
            if (view.IsTemplate || view is ViewSheet || view is ViewSchedule)
                return Fail($"View '{view.Name}' cannot host element tags.");

            var targets = CollectTargets(doc, view, parameters, out var skipped);
            var untaggedOnly = parameters.Value<bool?>("untaggedOnly") ?? true;
            if (untaggedOnly)
            {
                var tagged = TaggedElementIds(doc, view);
                foreach (var element in targets.Where(e => tagged.Contains(e.Id.GetValue())).ToList())
                {
                    targets.Remove(element);
                    skipped.Add(new { elementId = element.Id.GetValue(), reason = "Already tagged in this view." });
                }
            }

            var maxTags = Math.Max(1, Math.Min(parameters.Value<int?>("maxTags") ?? DefaultMaxTags, 5000));
            if (targets.Count > maxTags)
                return Fail($"{targets.Count} elements match, more than maxTags ({maxTags}). Narrow the request or raise maxTags.");

            var tagType = DocumentationUtils.GetElement<FamilySymbol>(doc, DocumentationUtils.ReadId(parameters, "tagTypeId"));
            if (DocumentationUtils.ReadId(parameters, "tagTypeId") != null && tagType == null)
                return Fail("'tagTypeId' does not refer to a tag family type.");
            var notes = new List<string>();
            var tagTypeName = parameters.Value<string>("tagTypeName");
            if (tagType == null && !string.IsNullOrWhiteSpace(tagTypeName))
                tagType = ResolveTagType(doc, tagTypeName, notes);

            var addLeader = parameters.Value<bool?>("addLeader") ?? false;
            var orientationText = parameters.Value<string>("orientation");
            var modelOrientation = string.Equals(orientationText?.Trim(), "Model", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(orientationText?.Trim(), "AnyModelDirection", StringComparison.OrdinalIgnoreCase);
            var orientation = modelOrientation
                ? TagOrientation.Horizontal
                : DocumentationUtils.ParseEnum(orientationText, TagOrientation.Horizontal);
            var offset = DocumentationUtils.ReadPointMm(parameters["offset"]) ?? XYZ.Zero;
            var normalOffset = DocumentationUtils.MmToFeet(parameters.Value<double?>("offsetAlongNormalMm") ?? 0);
            var avoidOverlaps = parameters.Value<bool?>("avoidOverlaps") ?? false;
            var maxShiftTries = Math.Max(1, Math.Min(parameters.Value<int?>("maxShiftTries") ?? 8, 40));
            var shiftStepMm = parameters.Value<double?>("shiftStepMm");
            var shiftStep = shiftStepMm != null && shiftStepMm > 0 ? DocumentationUtils.MmToFeet(shiftStepMm.Value) : (double?)null;

            var placement = ParsePlacement(parameters.Value<string>("placement"));
            var scale = Math.Max(1, view.Scale);
            var gap = DocumentationUtils.MmToFeet((parameters.Value<double?>("offsetPaperMm") ?? 2.0) * scale);
            var leaderMode = parameters.Value<string>("leader")?.Trim().ToLowerInvariant();
            if (leaderMode != null && leaderMode != "auto" && leaderMode != "none" && leaderMode != "always")
                return Fail("leader must be 'auto', 'none' or 'always'.");
            var leaderThreshold = DocumentationUtils.MmToFeet((parameters.Value<double?>("leaderThresholdPaperMm") ?? 5.0) * scale);
            // With a placement preset or a leader mode the leader is added after the tag has been positioned.
            var deferLeader = placement != null || leaderMode != null;
            var createLeader = !deferLeader && addLeader;
            var leadersAdded = 0;

            if (targets.Count == 0)
                return Ok("Nothing to tag.", new { viewId = view.Id.GetValue(), created = new object[0], skipped });

            var withoutNormal = 0;
#if !REVIT2022_OR_GREATER
            if (modelOrientation)
                notes.Add("orientation 'Model' needs Revit 2022+; each tag used Horizontal or Vertical, whichever is closer to the element.");
#endif
            var occupied = avoidOverlaps ? ExistingTagRects(doc, view) : new List<Rect>();
            var remainingOverlaps = new List<object>();
            var remainingCount = 0;
            var shiftedCount = 0;

            var items = new JArray(targets.Select(e => e.Id.GetValue()));
            var results = DocumentationUtils.RunBatch(doc, "MCP: Tag Elements", items, token =>
            {
                var element = doc.GetElement(token.Value<long>().ToRevitElementId());
                var anchor = AnchorPoint(element, view)
                             ?? throw new InvalidOperationException("Element has no location in this view.");
                var lineDirection = LinearDirection(element, view);
                var extra = offset;
                if (Math.Abs(normalOffset) > 1e-9)
                {
                    if (lineDirection != null) extra += Normal(lineDirection, view) * normalOffset;
                    else withoutNormal++;
                }
                // Placement presets measure the tag first, so it is created at the anchor and the extra offset is added afterwards.
                var head = placement == null ? anchor + extra : anchor;

                Element tagElement;
                if (element is Room room)
                {
                    if (!(view is ViewPlan) && !(view is ViewSection))
                        throw new InvalidOperationException("Room tags require a plan or section view.");
                    var roomTag = doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(head.X, head.Y), view.Id);
                    if (tagType != null)
                        roomTag.ChangeTypeId(tagType.Id);
                    tagElement = roomTag;
                }
                else
                {
                    if (element is SpatialElement)
                        throw new InvalidOperationException("Area and space tags are not supported by this tool.");

                    if (tagType != null && !tagType.IsActive)
                        tagType.Activate();

                    var direction = lineDirection ?? PointDirection(element, view);
                    var tagOrientation = orientation;
                    if (modelOrientation)
                    {
#if REVIT2022_OR_GREATER
                        // With a placement preset the tag is measured horizontal first and rotated in PlaceTag.
                        tagOrientation = placement == null ? TagOrientation.AnyModelDirection : TagOrientation.Horizontal;
#else
                        tagOrientation = direction != null && Math.Abs(direction.DotProduct(view.UpDirection)) > Math.Abs(direction.DotProduct(view.RightDirection))
                            ? TagOrientation.Vertical
                            : TagOrientation.Horizontal;
#endif
                    }

                    var reference = new Reference(element);
                    var tag = tagType != null
                        ? IndependentTag.Create(doc, tagType.Id, view.Id, reference, createLeader, tagOrientation, head)
                        : IndependentTag.Create(doc, view.Id, reference, createLeader, TagMode.TM_ADDBY_CATEGORY, tagOrientation, head);
#if REVIT2022_OR_GREATER
                    if (modelOrientation && direction != null && placement == null)
                        tag.RotationAngle = ReadableAngle(direction, view);
#endif
                    tagElement = tag;
                }

                if (placement != null)
                {
                    PlaceTag(doc, view, tagElement, element, anchor, lineDirection,
                        modelOrientation ? lineDirection ?? PointDirection(element, view) : null, placement, gap);
                    if (!extra.IsZeroLength())
                    {
                        var placedHead = HeadPosition(tagElement);
                        if (placedHead != null) MoveHead(doc, tagElement, placedHead + extra);
                    }
                }

                var result = new JObject
                {
                    ["elementId"] = element.Id.GetValue(),
                    ["tagId"] = tagElement.Id.GetValue()
                };

                var headBefore = HeadPosition(tagElement);
                if (avoidOverlaps)
                {
                    var placed = AvoidOverlap(doc, view, tagElement, lineDirection, occupied, maxShiftTries, shiftStep, out var rect, out var blocker, out var shifted);
                    if (shifted)
                    {
                        result["shifted"] = true;
                        shiftedCount++;
                    }
                    if (!placed)
                    {
                        result["overlapsTagId"] = blocker;
                        remainingCount++;
                        if (remainingOverlaps.Count < MaxReportedOverlaps)
                            remainingOverlaps.Add(new { elementId = element.Id.GetValue(), tagId = tagElement.Id.GetValue(), overlapsTagId = blocker });
                    }
                    if (rect != null) occupied.Add(rect);
                }

                if (deferLeader)
                {
                    var headAfter = HeadPosition(tagElement);
                    var wantLeader = leaderMode == "always"
                                     || (leaderMode == null && addLeader)
                                     || (leaderMode == "auto" && headBefore != null && headAfter != null &&
                                         ViewDistance(headBefore, headAfter, view) > leaderThreshold);
                    if (wantLeader && SetLeader(tagElement))
                    {
                        result["leader"] = true;
                        leadersAdded++;
                    }
                }

                return result;
            });

            var created = results.Where(r => r.Value<bool>("success")).ToList();
            foreach (var failure in results.Where(r => !r.Value<bool>("success")))
                skipped.Add(new { elementId = items[failure.Value<int>("index")].Value<long>(), reason = failure.Value<string>("message") });

            if (withoutNormal > 0)
                notes.Add($"offsetAlongNormalMm ignored for {withoutNormal} non-linear element(s).");

            var response = new JObject
            {
                ["viewId"] = view.Id.GetValue(),
                ["created"] = new JArray(created.Select(r =>
                {
                    var entry = new JObject { ["elementId"] = r["elementId"], ["tagId"] = r["tagId"] };
                    if (r["shifted"] != null) entry["shifted"] = r["shifted"];
                    if (r["overlapsTagId"] != null) entry["overlapsTagId"] = r["overlapsTagId"];
                    if (r["leader"] != null) entry["leader"] = r["leader"];
                    return entry;
                })),
                ["skipped"] = JArray.FromObject(skipped)
            };
            var message = $"Created {created.Count} tags in '{view.Name}'; skipped {skipped.Count}.";
            if (avoidOverlaps)
            {
                response["shifted"] = shiftedCount;
                response["remainingOverlapCount"] = remainingCount;
                response["remainingOverlaps"] = JArray.FromObject(remainingOverlaps);
                message += $" Shifted {shiftedCount} to avoid overlaps; {remainingCount} still overlap.";
            }
            if (placement != null) response["placement"] = placement;
            if (deferLeader) response["leadersAdded"] = leadersAdded;
            if (tagType != null) response["tagType"] = $"{tagType.FamilyName}: {tagType.Name}";
            if (notes.Count > 0) response["notes"] = new JArray(notes);
            return Ok(message, response);
        }

        // ---------------------------------------------------------------- placement presets

        private static readonly string[] Placements =
            { "center", "above", "below", "left", "right", "topRight", "topLeft", "bottomRight", "bottomLeft" };

        private static string ParsePlacement(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var match = Placements.FirstOrDefault(p => string.Equals(p, text.Trim(), StringComparison.OrdinalIgnoreCase));
            return match ?? throw new ArgumentException($"placement must be one of {string.Join(", ", Placements)}.");
        }

        /// <summary>
        ///     Moves a freshly created tag so its box lands at the preset position: linear elements
        ///     (center / above / below) centred on the element and offset along its in-view normal by
        ///     half the element width + gap + half the tag; otherwise relative to the element's box in
        ///     the view (gap in model units = offsetPaperMm x view scale). Model-oriented tags are
        ///     measured horizontal first, then rotated to the element direction.
        /// </summary>
        private static void PlaceTag(Document doc, View view, Element tag, Element element, XYZ anchor, XYZ lineDirection,
            XYZ modelDirection, string placement, double gap)
        {
            doc.Regenerate();
            var rect = TagRect(tag, view);
            if (rect == null) return;
            var textAcross = rect.MaxV - rect.MinV;
            if (tag is IndependentTag independent && independent.TagOrientation == TagOrientation.Vertical)
                textAcross = rect.MaxU - rect.MinU;

            var textAlongElement = false;
#if REVIT2022_OR_GREATER
            if (modelDirection != null && tag is IndependentTag rotatable)
            {
                rotatable.TagOrientation = TagOrientation.AnyModelDirection;
                rotatable.RotationAngle = ReadableAngle(modelDirection, view);
                doc.Regenerate();
                rect = TagRect(tag, view) ?? rect;
                textAlongElement = lineDirection != null;
            }
#else
            textAlongElement = modelDirection != null && lineDirection != null && tag is IndependentTag vertical &&
                               (vertical.TagOrientation == TagOrientation.Vertical) ==
                               (Math.Abs(lineDirection.DotProduct(view.UpDirection)) > Math.Abs(lineDirection.DotProduct(view.RightDirection)));
#endif
            var halfW = (rect.MaxU - rect.MinU) / 2;
            var halfH = (rect.MaxV - rect.MinV) / 2;
            double targetU, targetV;

            if (lineDirection != null && (placement == "center" || placement == "above" || placement == "below"))
            {
                var target = anchor;
                if (placement != "center")
                {
                    var normal = Normal(lineDirection, view);
                    var extent = textAlongElement
                        ? textAcross / 2
                        : Math.Abs(normal.DotProduct(view.RightDirection)) * halfW + Math.Abs(normal.DotProduct(view.UpDirection)) * halfH;
                    var distance = HalfWidth(element, view, normal) + gap + extent;
                    target += normal * (placement == "above" ? distance : -distance);
                }
                targetU = target.DotProduct(view.RightDirection);
                targetV = target.DotProduct(view.UpDirection);
            }
            else
            {
                var au = anchor.DotProduct(view.RightDirection);
                var av = anchor.DotProduct(view.UpDirection);
                var box = TagRect(element, view) ?? new Rect { MinU = au, MaxU = au, MinV = av, MaxV = av };
                var cu = (box.MinU + box.MaxU) / 2;
                var cv = (box.MinV + box.MaxV) / 2;
                var right = box.MaxU + gap + halfW;
                var left = box.MinU - gap - halfW;
                var top = box.MaxV + gap + halfH;
                var bottom = box.MinV - gap - halfH;
                switch (placement)
                {
                    case "above": targetU = cu; targetV = top; break;
                    case "below": targetU = cu; targetV = bottom; break;
                    case "left": targetU = left; targetV = cv; break;
                    case "right": targetU = right; targetV = cv; break;
                    case "topRight": targetU = right; targetV = top; break;
                    case "topLeft": targetU = left; targetV = top; break;
                    case "bottomRight": targetU = right; targetV = bottom; break;
                    case "bottomLeft": targetU = left; targetV = bottom; break;
                    default: targetU = cu; targetV = cv; break;
                }
            }

            var head = HeadPosition(tag);
            if (head == null) return;
            var currentU = (rect.MinU + rect.MaxU) / 2;
            var currentV = (rect.MinV + rect.MaxV) / 2;
            MoveHead(doc, tag, head + view.RightDirection * (targetU - currentU) + view.UpDirection * (targetV - currentV));
        }

        /// <summary>Half the width of a linear element across its line, in model feet (walls: Width; beams: section width).</summary>
        private static double HalfWidth(Element element, View view, XYZ normal)
        {
            if (element is Wall wall) return wall.Width / 2;
            if (element is FamilyInstance instance)
            {
                var type = element.Document.GetElement(element.GetTypeId());
                var width = PositiveLength(type?.get_Parameter(BuiltInParameter.STRUCTURAL_SECTION_COMMON_WIDTH));
                foreach (var name in new[] { "b", "Width", "B", "bf" })
                {
                    if (width != null) break;
                    width = PositiveLength(instance.LookupParameter(name)) ?? PositiveLength(type?.LookupParameter(name));
                }
                if (width != null) return width.Value / 2;
            }

            // Fallback: the element's box in the view, only exact for elements along the view axes.
            var u = Math.Abs(normal.DotProduct(view.RightDirection));
            var v = Math.Abs(normal.DotProduct(view.UpDirection));
            if (u > 1e-3 && v > 1e-3) return 0;
            var box = TagRect(element, view);
            return box == null ? 0 : (u > v ? box.MaxU - box.MinU : box.MaxV - box.MinV) / 2;
        }

        private static double? PositiveLength(Parameter parameter) =>
            parameter != null && parameter.StorageType == StorageType.Double && parameter.AsDouble() > 1e-6 ? parameter.AsDouble() : (double?)null;

        private static double ViewDistance(XYZ a, XYZ b, View view)
        {
            var d = b - a;
            var u = d.DotProduct(view.RightDirection);
            var v = d.DotProduct(view.UpDirection);
            return Math.Sqrt(u * u + v * v);
        }

        private static bool SetLeader(Element tag)
        {
            try
            {
                if (tag is IndependentTag independent)
                {
                    independent.HasLeader = true;
                    return true;
                }
                if (tag is SpatialElementTag spatial)
                {
                    spatial.HasLeader = true;
                    return true;
                }
            }
            catch (Exception)
            {
                // Some tag types cannot have a leader.
            }
            return false;
        }

        /// <summary>Tag type from "Family: Type", a type name or a family name (annotation family symbols only).</summary>
        private static FamilySymbol ResolveTagType(Document doc, string text, List<string> notes)
        {
            var candidates = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category != null && s.Category.CategoryType == CategoryType.Annotation)
                .OrderBy(s => s.FamilyName, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal).ToList();
            var trimmed = text.Trim();
            var colon = trimmed.IndexOf(':');
            var matches = new List<FamilySymbol>();
            if (colon > 0)
            {
                var family = trimmed.Substring(0, colon).Trim();
                var type = trimmed.Substring(colon + 1).Trim();
                matches = candidates.Where(s => string.Equals(s.FamilyName, family, StringComparison.OrdinalIgnoreCase) &&
                                                string.Equals(s.Name, type, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (matches.Count == 0)
                matches = candidates.Where(s => string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
                matches = candidates.Where(s => string.Equals(s.FamilyName, trimmed, StringComparison.OrdinalIgnoreCase)).ToList();

            if (matches.Count == 0)
                throw new ArgumentException($"Tag type '{text}' not found. Use 'Family: Type', e.g. " +
                                            string.Join(", ", candidates.Where(s => s.Category.Name.IndexOf("Tag", StringComparison.OrdinalIgnoreCase) >= 0)
                                                .Take(15).Select(s => $"{s.FamilyName}: {s.Name}")));
            if (matches.Count > 1)
                notes.Add($"tagTypeName '{text}' matches {matches.Count} types; used '{matches[0].FamilyName}: {matches[0].Name}'.");
            return matches[0];
        }

        // ---------------------------------------------------------------- placement helpers

        /// <summary>Unit direction of a linear element in the view plane, turned to read left-to-right / bottom-to-top.</summary>
        private static XYZ LinearDirection(Element element, View view)
        {
            if (!(element.Location is LocationCurve locationCurve) || locationCurve.Curve == null) return null;
            XYZ direction;
            var curve = locationCurve.Curve;
            if (curve is Line line) direction = line.Direction;
            else
            {
                try
                {
                    direction = curve.ComputeDerivatives(0.5, true).BasisX;
                }
                catch (Exception)
                {
                    direction = curve.GetEndPoint(1) - curve.GetEndPoint(0);
                }
            }
            return Readable(direction, view);
        }

        /// <summary>Hand orientation of a point-based family instance in the view plane (for model-oriented tags).</summary>
        private static XYZ PointDirection(Element element, View view)
        {
            if (!(element is FamilyInstance instance) || !(element.Location is LocationPoint)) return null;
            try
            {
                return Readable(instance.HandOrientation, view);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static XYZ Readable(XYZ direction, View view)
        {
            if (direction == null) return null;
            var u = direction.DotProduct(view.RightDirection);
            var v = direction.DotProduct(view.UpDirection);
            var length = Math.Sqrt(u * u + v * v);
            if (length < 1e-6) return null; // perpendicular to the view plane
            u /= length;
            v /= length;
            // Keep text readable: angle in (-90, 90] degrees.
            if (u < -1e-9 || (Math.Abs(u) <= 1e-9 && v < 0))
            {
                u = -u;
                v = -v;
            }
            return view.RightDirection * u + view.UpDirection * v;
        }

        /// <summary>In-view normal on the side the text top faces (above a horizontal element, left of a vertical one).</summary>
        private static XYZ Normal(XYZ readableDirection, View view)
        {
            var u = readableDirection.DotProduct(view.RightDirection);
            var v = readableDirection.DotProduct(view.UpDirection);
            return view.RightDirection * -v + view.UpDirection * u;
        }

        private static double ReadableAngle(XYZ readableDirection, View view) =>
            Math.Atan2(readableDirection.DotProduct(view.UpDirection), readableDirection.DotProduct(view.RightDirection));

        private static List<Rect> ExistingTagRects(Document doc, View view)
        {
            var rects = new List<Rect>();
            var tags = new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).ToElements()
                .Concat(new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsNotElementType().ToElements());
            foreach (var tag in tags)
            {
                var rect = TagRect(tag, view);
                if (rect != null) rects.Add(rect);
            }
            return rects;
        }

        private static Rect TagRect(Element tag, View view)
        {
            BoundingBoxXYZ box;
            try
            {
                box = tag.get_BoundingBox(view);
            }
            catch (Exception)
            {
                return null;
            }
            if (box == null) return null;

            var transform = box.Transform ?? Transform.Identity;
            var rect = new Rect
            {
                Id = tag.Id.GetValue(), MinU = double.MaxValue, MinV = double.MaxValue, MaxU = double.MinValue, MaxV = double.MinValue
            };
            foreach (var x in new[] { box.Min.X, box.Max.X })
            foreach (var y in new[] { box.Min.Y, box.Max.Y })
            foreach (var z in new[] { box.Min.Z, box.Max.Z })
            {
                var point = transform.OfPoint(new XYZ(x, y, z));
                var u = point.DotProduct(view.RightDirection);
                var v = point.DotProduct(view.UpDirection);
                rect.MinU = Math.Min(rect.MinU, u);
                rect.MaxU = Math.Max(rect.MaxU, u);
                rect.MinV = Math.Min(rect.MinV, v);
                rect.MaxV = Math.Max(rect.MaxV, v);
            }
            return rect;
        }

        private static Rect FirstOverlap(Rect rect, List<Rect> occupied) =>
            rect == null ? null : occupied.FirstOrDefault(o => o.Id != rect.Id && rect.Overlaps(o));

        private static XYZ HeadPosition(Element tag) =>
            tag is IndependentTag independent ? independent.TagHeadPosition : (tag.Location as LocationPoint)?.Point;

        private static void MoveHead(Document doc, Element tag, XYZ target)
        {
            if (tag is IndependentTag independent)
            {
                independent.TagHeadPosition = target;
                return;
            }
            var current = HeadPosition(tag);
            if (current != null) ElementTransformUtils.MoveElement(doc, tag.Id, target - current);
        }

        /// <summary>
        ///     Shifts the tag perpendicular to / along the element (or the view's up / right)
        ///     until its box no longer overlaps an occupied box. Returns false when every tried
        ///     position overlaps; the tag is then left at its original position.
        /// </summary>
        private static bool AvoidOverlap(Document doc, View view, Element tag, XYZ lineDirection, List<Rect> occupied,
            int maxTries, double? step, out Rect rect, out long? blocker, out bool shifted)
        {
            shifted = false;
            blocker = null;
            doc.Regenerate();
            rect = TagRect(tag, view);
            var hit = FirstOverlap(rect, occupied);
            if (rect == null || hit == null) return true;

            var start = HeadPosition(tag);
            if (start == null)
            {
                blocker = hit.Id;
                return false;
            }

            var along = lineDirection ?? view.RightDirection;
            var normal = lineDirection != null ? Normal(lineDirection, view) : view.UpDirection;
            var width = rect.MaxU - rect.MinU;
            var height = rect.MaxV - rect.MinV;
            double Extent(XYZ d) => Math.Abs(d.DotProduct(view.RightDirection)) * width + Math.Abs(d.DotProduct(view.UpDirection)) * height;
            var stepNormal = step ?? Math.Max(Extent(normal) * 1.1, 1e-3);
            var stepAlong = step ?? Math.Max(Extent(along) * 1.1, 1e-3);

            var tried = 0;
            for (var k = 1; tried < maxTries; k++)
            {
                foreach (var delta in new[] { normal * (k * stepNormal), normal * (-k * stepNormal), along * (k * stepAlong), along * (-k * stepAlong) })
                {
                    if (tried >= maxTries) break;
                    tried++;
                    MoveHead(doc, tag, start + delta);
                    doc.Regenerate();
                    var moved = TagRect(tag, view);
                    if (moved != null && FirstOverlap(moved, occupied) == null)
                    {
                        rect = moved;
                        shifted = true;
                        return true;
                    }
                }
            }

            MoveHead(doc, tag, start);
            doc.Regenerate();
            rect = TagRect(tag, view) ?? rect;
            blocker = hit.Id;
            return false;
        }

        // ---------------------------------------------------------------- targets

        private static List<Element> CollectTargets(Document doc, View view, JObject parameters, out List<object> skipped)
        {
            skipped = new List<object>();
            var targets = new List<Element>();

            if (parameters["elementIds"] is JArray ids && ids.Count > 0)
            {
                foreach (var id in ids.Select(t => t.Value<long>()).Distinct())
                {
                    var element = doc.GetElement(id.ToRevitElementId());
                    if (element == null || element is ElementType)
                        skipped.Add(new { elementId = id, reason = "Not an element instance." });
                    else
                        targets.Add(element);
                }

                return targets;
            }

            var categories = parameters["categories"]?.ToObject<List<string>>() ?? new List<string>();
            if (categories.Count == 0)
                throw new ArgumentException("Provide 'elementIds' or 'categories'.");

            foreach (var name in categories)
            {
                var category = DocumentationUtils.ResolveCategory(doc, name)
                               ?? throw new ArgumentException($"Category '{name}' not found.");
                targets.AddRange(new FilteredElementCollector(doc, view.Id)
                    .OfCategoryId(category.Id)
                    .WhereElementIsNotElementType()
                    .ToElements());
            }

            return targets.GroupBy(e => e.Id.GetValue()).Select(g => g.First()).OrderBy(e => e.Id.GetValue()).ToList();
        }

        private static HashSet<long> TaggedElementIds(Document doc, View view)
        {
            var tagged = new HashSet<long>();
            foreach (var tag in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
#if REVIT2022_OR_GREATER
                foreach (var id in tag.GetTaggedLocalElementIds())
                    tagged.Add(id.GetValue());
#else
                tagged.Add(tag.TaggedLocalElementId.GetValue());
#endif
            }

            foreach (var roomTag in new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_RoomTags)
                         .WhereElementIsNotElementType().OfType<RoomTag>())
            {
                tagged.Add(roomTag.TaggedLocalRoomId.GetValue());
            }

            return tagged;
        }

        internal static XYZ AnchorPoint(Element element, View view)
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
    }
}
