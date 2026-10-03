using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Library
{
    /// <summary>
    ///     Places live references to existing views: reference callouts
    ///     (<see cref="ViewSection.CreateReferenceCallout" />) and reference
    ///     sections (<see cref="ViewSection.CreateReferenceSection" />). Both
    ///     work in drafting parent views and can target drafting views, so the
    ///     marker reports the target's sheet and detail number automatically.
    ///     The "View Reference" annotation family has no creation API.
    /// </summary>
    public class CreateViewReferenceEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Create View Reference";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "references");
            var results = DocumentationUtils.RunBatch(doc, "MCP: Create View References", items,
                item => Create(doc, (JObject)item));
            return Ok($"Created {results.Count(r => r.Value<bool>("success"))} of {results.Count} view references.",
                DocumentationUtils.Summarize(results));
        }

        private static object Create(Document doc, JObject item)
        {
            var parent = DocumentationUtils.GetElement<View>(doc, DocumentationUtils.ReadId(item, "parentViewId"))
                         ?? throw new ArgumentException("'parentViewId' does not refer to a view.");
            var target = DocumentationUtils.GetElement<View>(doc, DocumentationUtils.ReadId(item, "targetViewId"))
                         ?? throw new ArgumentException("'targetViewId' does not refer to a view.");
            if (parent.IsTemplate || target.IsTemplate)
                throw new ArgumentException("View templates cannot host or be the target of a reference.");
            if (parent.Id == target.Id)
                throw new ArgumentException("The parent and target views must differ.");

            var kind = (item.Value<string>("kind") ?? "callout").Trim().ToLowerInvariant();
            var first = DocumentationUtils.ReadPointMm(item["start"])
                        ?? throw new ArgumentException("'start' is required (mm).");
            var second = DocumentationUtils.ReadPointMm(item["end"])
                         ?? throw new ArgumentException("'end' is required (mm).");

            ICollection<ElementId> before;
            switch (kind)
            {
                case "callout":
                    before = parent.GetReferenceCallouts();
                    ViewSection.CreateReferenceCallout(doc, parent.Id, target.Id, first, second);
                    break;
                case "section":
                    before = parent.GetReferenceSections();
                    ViewSection.CreateReferenceSection(doc, parent.Id, target.Id, first, second);
                    break;
                default:
                    throw new ArgumentException("'kind' must be 'callout' or 'section'.");
            }

            doc.Regenerate();
            var after = kind == "callout" ? parent.GetReferenceCallouts() : parent.GetReferenceSections();
            var known = new HashSet<long>(before.Select(id => id.GetValue()));
            var created = after.Select(id => id.GetValue()).Where(id => !known.Contains(id)).ToList();

            var placement = ViewportIndex.Build(doc).FindView(target.Id);
            return new
            {
                kind,
                elementIds = created,
                parentViewId = parent.Id.GetValue(),
                parentViewName = parent.Name,
                targetViewId = target.Id.GetValue(),
                targetViewName = target.Name,
                targetSheetNumber = placement?.Sheet.SheetNumber,
                targetDetailNumber = placement?.DetailNumber
            };
        }
    }
}
