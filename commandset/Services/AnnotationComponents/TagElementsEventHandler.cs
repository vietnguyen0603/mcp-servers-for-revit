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
    /// </summary>
    public class TagElementsEventHandler : JsonParameterEventHandler
    {
        private const int DefaultMaxTags = 500;

        public override string GetName() => "Tag Elements";

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

            var addLeader = parameters.Value<bool?>("addLeader") ?? false;
            var orientation = DocumentationUtils.ParseEnum(parameters.Value<string>("orientation"), TagOrientation.Horizontal);
            var offset = DocumentationUtils.ReadPointMm(parameters["offset"]) ?? XYZ.Zero;

            if (targets.Count == 0)
                return Ok("Nothing to tag.", new { viewId = view.Id.GetValue(), created = new object[0], skipped });

            var items = new JArray(targets.Select(e => e.Id.GetValue()));
            var results = DocumentationUtils.RunBatch(doc, "MCP: Tag Elements", items, token =>
            {
                var element = doc.GetElement(token.Value<long>().ToRevitElementId());
                var anchor = AnchorPoint(element, view)
                             ?? throw new InvalidOperationException("Element has no location in this view.");
                var head = anchor + offset;

                if (element is Room room)
                {
                    if (!(view is ViewPlan) && !(view is ViewSection))
                        throw new InvalidOperationException("Room tags require a plan or section view.");
                    var roomTag = doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(head.X, head.Y), view.Id);
                    if (tagType != null)
                        roomTag.ChangeTypeId(tagType.Id);
                    return new { elementId = element.Id.GetValue(), tagId = roomTag.Id.GetValue() };
                }

                if (element is SpatialElement)
                    throw new InvalidOperationException("Area and space tags are not supported by this tool.");

                if (tagType != null && !tagType.IsActive)
                    tagType.Activate();

                var reference = new Reference(element);
                var tag = tagType != null
                    ? IndependentTag.Create(doc, tagType.Id, view.Id, reference, addLeader, orientation, head)
                    : IndependentTag.Create(doc, view.Id, reference, addLeader, TagMode.TM_ADDBY_CATEGORY, orientation, head);

                return new { elementId = element.Id.GetValue(), tagId = tag.Id.GetValue() };
            });

            var created = results.Where(r => r.Value<bool>("success")).ToList();
            foreach (var failure in results.Where(r => !r.Value<bool>("success")))
                skipped.Add(new { elementId = items[failure.Value<int>("index")].Value<long>(), reason = failure.Value<string>("message") });

            return Ok($"Created {created.Count} tags in '{view.Name}'; skipped {skipped.Count}.", new
            {
                viewId = view.Id.GetValue(),
                created = created.Select(r => new { elementId = r.Value<long>("elementId"), tagId = r.Value<long>("tagId") }),
                skipped
            });
        }

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

        private static XYZ AnchorPoint(Element element, View view)
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
