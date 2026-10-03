using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services.Views;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Library
{
    /// <summary>
    ///     Insert-Views-from-File equivalent: for each drafting view of another
    ///     open document, creates a drafting view with the same name and scale
    ///     in the active document and copies its contents, reusing destination
    ///     types on name clashes. One undo step; per-view results.
    /// </summary>
    public class CopyDraftingViewsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Copy Drafting Views";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var title = parameters.Value<string>("sourceDocumentTitle");
            if (string.IsNullOrWhiteSpace(title))
                return Fail("'sourceDocumentTitle' is required.");
            var sourceDoc = ViewCopyUtils.FindDocument(uiDoc.Application.Application, doc, title);
            var crossDocument = !sourceDoc.Equals(doc);

            var items = new JArray();
            if (parameters["viewIds"] is JArray viewIds)
                foreach (var id in viewIds)
                    items.Add(id);
            if (parameters["viewNames"] is JArray viewNames)
                foreach (var name in viewNames)
                    items.Add(name);
            if (items.Count == 0)
                return Fail("Provide 'viewIds' or 'viewNames'.");

            var rename = string.Equals(parameters.Value<string>("nameConflict") ?? "skip", "rename",
                StringComparison.OrdinalIgnoreCase);
            var viewFamilyTypeId = CreateViewEventHandler.ResolveViewFamilyType(doc, parameters, ViewFamily.Drafting);

            var sourceDrafting = new FilteredElementCollector(sourceDoc)
                .OfClass(typeof(ViewDrafting))
                .Cast<ViewDrafting>()
                .Where(v => !v.IsTemplate)
                .ToList();
            var usedNames = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).Select(v => v.Name),
                StringComparer.OrdinalIgnoreCase);

            var results = DocumentationUtils.RunBatch(doc, "MCP: Copy Drafting Views", items, token =>
            {
                var source = Resolve(sourceDoc, sourceDrafting, token);
                var name = source.Name;
                if (usedNames.Contains(name))
                {
                    if (!rename)
                        return new { skipped = true, sourceViewId = source.Id.GetValue(), sourceViewName = name, reason = "name exists" };
                    name = UniqueName(name, usedNames);
                }

                var view = ViewDrafting.Create(doc, viewFamilyTypeId);
                view.Name = name;
                if (view.Scale != source.Scale)
                    view.Scale = source.Scale;
                TryCopyViewSettings(source, view);

                var ids = ViewCopyUtils.CopyableElements(sourceDoc, source, crossDocument);
                var copied = ids.Count == 0
                    ? new List<ElementId>()
                    : ElementTransformUtils.CopyElements(source, ids, view, Transform.Identity,
                        ViewCopyUtils.DestinationTypeOptions());
                usedNames.Add(name);

                return new
                {
                    skipped = false,
                    sourceViewId = source.Id.GetValue(),
                    sourceViewName = source.Name,
                    viewId = view.Id.GetValue(),
                    name = view.Name,
                    scale = view.Scale,
                    sourceElementCount = ids.Count,
                    newElementCount = copied.Count
                };
            });

            var created = results.Count(r => r.Value<bool>("success") && !(r.Value<bool?>("skipped") ?? false));
            var skippedCount = results.Count(r => r.Value<bool?>("skipped") ?? false);
            return Ok($"Created {created} drafting views from '{sourceDoc.Title}'; {skippedCount} skipped on name clashes.",
                DocumentationUtils.Summarize(results));
        }

        private static ViewDrafting Resolve(Document sourceDoc, List<ViewDrafting> drafting, JToken token)
        {
            if (token.Type == JTokenType.Integer)
            {
                var id = token.Value<long>();
                return sourceDoc.GetElement(id.ToRevitElementId()) as ViewDrafting
                       ?? throw new ArgumentException($"{id} is not a drafting view in '{sourceDoc.Title}'.");
            }

            var name = token.ToString().Trim();
            return drafting.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.Ordinal))
                   ?? drafting.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"No drafting view named '{name}' in '{sourceDoc.Title}'.");
        }

        private static string UniqueName(string name, HashSet<string> used)
        {
            for (var i = 2;; i++)
            {
                var candidate = $"{name} ({i})";
                if (!used.Contains(candidate))
                    return candidate;
            }
        }

        /// <summary>Best effort: detail level and "Title on Sheet".</summary>
        private static void TryCopyViewSettings(View source, View target)
        {
            try
            {
                if (target.CanModifyDetailLevel() && target.DetailLevel != source.DetailLevel)
                    target.DetailLevel = source.DetailLevel;
            }
            catch (Exception)
            {
                // Not every view type allows a detail level change.
            }

            var title = source.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION)?.AsString();
            var targetTitle = target.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION);
            if (!string.IsNullOrEmpty(title) && targetTitle != null && !targetTitle.IsReadOnly)
                targetTitle.Set(title);
        }
    }
}
