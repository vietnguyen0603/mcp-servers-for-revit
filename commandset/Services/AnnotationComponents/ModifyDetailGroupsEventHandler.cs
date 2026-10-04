using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Places, creates and ungroups detail groups. Placing copies an existing instance of the
    ///     type into the target view (Document.PlaceGroup has no view argument), then moves it so
    ///     its origin lands on the location; PlaceGroup is only a fallback for types without any
    ///     instance, in the active view. All operations are one undo step; each reports its own result.
    /// </summary>
    public class ModifyDetailGroupsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Modify Detail Groups";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var operations = DocumentationUtils.RequireArray(parameters, "operations");
            var defaultViewId = DocumentationUtils.ReadId(parameters, "viewId");
            var activeView = uiDoc.ActiveView;

            var results = DocumentationUtils.RunBatch(doc, "MCP: Modify Detail Groups", operations,
                op => Apply(doc, activeView, defaultViewId, (JObject)op));

            return Ok($"Applied {results.Count(r => r.Value<bool>("success"))} of {results.Count} operations.",
                DocumentationUtils.Summarize(results));
        }

        private static object Apply(Document doc, View activeView, long? defaultViewId, JObject op)
        {
            var action = op.Value<string>("action") ?? throw new ArgumentException("'action' is required.");
            switch (action)
            {
                case "place":
                    return Place(doc, activeView, defaultViewId, op);

                case "create":
                {
                    var elements = ReadElements(doc, op);
                    var ownerView = elements[0].OwnerViewId;
                    if (elements.Any(e => e.OwnerViewId == ElementId.InvalidElementId || e.OwnerViewId != ownerView))
                        throw new ArgumentException("Detail group members must be view-specific elements of one view.");
                    var group = doc.Create.NewGroup(elements.Select(e => e.Id).ToList());
                    var name = op.Value<string>("name");
                    if (!string.IsNullOrWhiteSpace(name))
                        group.GroupType.Name = name.Trim();
                    return new
                    {
                        action,
                        groupId = group.Id.GetValue(),
                        groupTypeId = group.GroupType.Id.GetValue(),
                        name = group.GroupType.Name,
                        memberIds = group.GetMemberIds().Select(i => i.GetValue())
                    };
                }

                case "ungroup":
                {
                    var ungrouped = ReadElements(doc, op).Select(e =>
                    {
                        var group = e as Group
                                    ?? throw new ArgumentException($"Element {e.Id.GetValue()} is not a group.");
                        var groupId = group.Id.GetValue();
                        return new { groupId, memberIds = group.UngroupMembers().Select(i => i.GetValue()).ToList() };
                    }).ToList();
                    return new { action, ungrouped };
                }

                default:
                    throw new ArgumentException($"Unknown action '{action}'. Use place, create or ungroup.");
            }
        }

        private static object Place(Document doc, View activeView, long? defaultViewId, JObject op)
        {
            var type = ResolveGroupType(doc, op);
            var viewId = DocumentationUtils.ReadId(op, "viewId") ?? defaultViewId;
            var view = viewId != null
                ? DocumentationUtils.GetElement<View>(doc, viewId) ?? throw new ArgumentException($"viewId {viewId} is not a view.")
                : activeView;
            DetailGeometry.RequireDetailView(view);
            var location = DetailGeometry.ReadPoint(view, op["location"], "location");

            var source = FindSource(doc, type, view, DocumentationUtils.ReadId(op, "sourceInstanceId"));
            Group placed;
            string method;
            if (source == null)
            {
                if (view.Id != activeView.Id)
                    throw new ArgumentException(
                        $"Detail group type '{type.Name}' has no placed instance to copy; place it in the active view or open '{view.Name}'.");
                placed = doc.Create.PlaceGroup(location, type);
                if (placed == null || placed.OwnerViewId != view.Id)
                    throw new InvalidOperationException($"PlaceGroup did not create '{type.Name}' in '{view.Name}'.");
                method = "placeGroup";
            }
            else if (source.OwnerViewId == view.Id)
            {
                var copied = ElementTransformUtils.CopyElements(doc, new List<ElementId> { source.Id }, location - Origin(source));
                placed = copied.Select(id => doc.GetElement(id)).OfType<Group>().FirstOrDefault()
                         ?? throw new InvalidOperationException("Copying the source group did not create a group.");
                method = "copyInView";
            }
            else
            {
                var sourceView = (View)doc.GetElement(source.OwnerViewId);
                var copied = ElementTransformUtils.CopyElements(sourceView, new List<ElementId> { source.Id }, view,
                    Transform.Identity, new CopyPasteOptions());
                placed = copied.Select(id => doc.GetElement(id)).OfType<Group>().FirstOrDefault()
                         ?? throw new InvalidOperationException("Copying the source group did not create a group.");
                var delta = location - Origin(placed);
                if (!delta.IsZeroLength())
                    ElementTransformUtils.MoveElement(doc, placed.Id, delta);
                method = "copyFromView";
            }

            return new
            {
                action = "place",
                groupId = placed.Id.GetValue(),
                groupTypeId = type.Id.GetValue(),
                name = type.Name,
                viewId = view.Id.GetValue(),
                sourceInstanceId = source?.Id.GetValue(),
                method,
                location = DocumentationUtils.PointToMm(Origin(placed))
            };
        }

        private static GroupType ResolveGroupType(Document doc, JObject op)
        {
            var typeId = DocumentationUtils.ReadId(op, "groupTypeId");
            if (typeId != null)
            {
                var byId = DocumentationUtils.GetElement<GroupType>(doc, typeId);
                if (byId == null || !DocumentationUtils.IsCategory(byId, BuiltInCategory.OST_IOSDetailGroups))
                    throw new ArgumentException($"groupTypeId {typeId} is not a detail group type.");
                return byId;
            }

            var name = op.Value<string>("groupTypeName")?.Trim();
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("place needs 'groupTypeId' or 'groupTypeName'.");
            return DetailGroupUtils.DetailGroupTypes(doc)
                       .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"Detail group type '{name}' not found; use list_detail_groups.");
        }

        /// <summary>Explicit source instance, else an instance in the target view, else the lowest-id instance.</summary>
        private static Group FindSource(Document doc, GroupType type, View view, long? sourceId)
        {
            if (sourceId != null)
            {
                var explicitSource = DocumentationUtils.GetElement<Group>(doc, sourceId);
                if (explicitSource == null || explicitSource.GroupType.Id != type.Id)
                    throw new ArgumentException($"sourceInstanceId {sourceId} is not an instance of '{type.Name}'.");
                return explicitSource;
            }

            var instances = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_IOSDetailGroups)
                .WhereElementIsNotElementType()
                .OfType<Group>()
                .Where(g => g.GroupType.Id == type.Id && g.OwnerViewId != ElementId.InvalidElementId)
                .OrderBy(g => g.Id.GetValue())
                .ToList();
            return instances.FirstOrDefault(g => g.OwnerViewId == view.Id) ?? instances.FirstOrDefault();
        }

        private static XYZ Origin(Group group)
        {
            return (group.Location as LocationPoint)?.Point
                   ?? throw new InvalidOperationException($"Group {group.Id.GetValue()} has no location point.");
        }

        private static List<Element> ReadElements(Document doc, JObject op)
        {
            var ids = op["elementIds"]?.ToObject<List<long>>() ?? new List<long>();
            if (ids.Count == 0)
                throw new ArgumentException("'elementIds' must list at least one element.");
            var elements = ids.Distinct().Select(id => doc.GetElement(id.ToRevitElementId())).ToList();
            var missing = ids.Distinct().Where((id, i) => elements[i] == null || elements[i] is ElementType).ToList();
            if (missing.Count > 0)
                throw new ArgumentException($"Elements not found: {string.Join(", ", missing)}.");
            return elements;
        }
    }
}
