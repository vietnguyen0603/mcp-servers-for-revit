using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Finds tags that no longer reference an element and, unless
    ///     <c>dryRun</c> is true (the default), deletes them in one undoable
    ///     transaction. A tag is orphaned when Revit reports it as orphaned or
    ///     every tagged reference is a local element that no longer exists.
    ///     Tags of linked elements are only reported when Revit flags them.
    /// </summary>
    public class DeleteOrphanedTagsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Delete Orphaned Tags";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var dryRun = parameters.Value<bool?>("dryRun") ?? true;

            HashSet<long> viewFilter = null;
            if (parameters["viewIds"] is JArray viewTokens && viewTokens.Count > 0)
            {
                viewFilter = new HashSet<long>();
                foreach (var id in viewTokens.Select(t => t.Value<long>()))
                {
                    if (!(doc.GetElement(id.ToRevitElementId()) is View))
                        return Fail($"viewId {id} does not refer to a view.");
                    viewFilter.Add(id);
                }
            }

            var orphans = new List<(Element tag, string reason)>();

            foreach (var tag in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                if (viewFilter != null && !viewFilter.Contains(tag.OwnerViewId.GetValue()))
                    continue;
                var reason = IndependentTagOrphanReason(doc, tag);
                if (reason != null)
                    orphans.Add((tag, reason));
            }

            foreach (var tag in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_RoomTags)
                         .WhereElementIsNotElementType().OfType<RoomTag>())
            {
                if (viewFilter != null && !viewFilter.Contains(tag.OwnerViewId.GetValue()))
                    continue;
                if (tag.IsOrphaned)
                    orphans.Add((tag, "Revit reports the room tag as orphaned."));
                else if (tag.TaggedLocalRoomId == ElementId.InvalidElementId && tag.TaggedRoomId.LinkInstanceId == ElementId.InvalidElementId)
                    orphans.Add((tag, "Room tag has no room."));
                else if (tag.TaggedLocalRoomId != ElementId.InvalidElementId && doc.GetElement(tag.TaggedLocalRoomId) == null)
                    orphans.Add((tag, "Tagged room no longer exists."));
            }

            var report = orphans
                .OrderBy(o => o.tag.OwnerViewId.GetValue())
                .ThenBy(o => o.tag.Id.GetValue())
                .Select(o => new
                {
                    tagId = o.tag.Id.GetValue(),
                    category = o.tag.Category?.Name,
                    viewId = o.tag.OwnerViewId.GetValue(),
                    viewName = DocumentationUtils.ViewName(doc, o.tag.OwnerViewId),
                    reason = o.reason
                })
                .ToList();

            if (dryRun || report.Count == 0)
            {
                return Ok(dryRun
                    ? $"Found {report.Count} orphaned tags (dry run, nothing deleted)."
                    : "No orphaned tags found.", new { dryRun, found = report.Count, deleted = new long[0], tags = report });
            }

            var ids = orphans.Select(o => o.tag.Id).ToList();
            using (var transaction = DocumentationUtils.StartTransaction(doc, "MCP: Delete Orphaned Tags"))
            {
                doc.Delete(ids);
                var status = transaction.Commit();
                if (status != TransactionStatus.Committed)
                    return Fail($"Delete transaction was not committed ({status}).");
            }

            return Ok($"Deleted {ids.Count} orphaned tags.", new
            {
                dryRun,
                found = report.Count,
                deleted = ids.Select(id => id.GetValue()).ToList(),
                tags = report
            });
        }

        private static string IndependentTagOrphanReason(Document doc, IndependentTag tag)
        {
            if (tag.IsOrphaned)
                return "Revit reports the tag as orphaned.";

#if REVIT2022_OR_GREATER
            var references = tag.GetTaggedElementIds();
#else
            var references = new List<LinkElementId> { tag.TaggedElementId };
#endif
            if (references == null || references.Count == 0)
                return "Tag references no elements.";

            foreach (var reference in references)
            {
                if (reference == null)
                    continue;
                // Linked references are resolved by Revit; trust IsOrphaned for those.
                if (reference.LinkInstanceId != ElementId.InvalidElementId)
                    return null;
                if (reference.HostElementId != ElementId.InvalidElementId && doc.GetElement(reference.HostElementId) != null)
                    return null;
            }

            return "Tagged element no longer exists.";
        }
    }
}
