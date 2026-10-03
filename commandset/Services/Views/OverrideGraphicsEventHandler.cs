using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Applies (or resets) graphic overrides and visibility for elements and
    ///     categories in one view. Each target is applied in its own
    ///     sub-transaction so one failure does not discard the others.
    /// </summary>
    public class OverrideGraphicsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Override Graphics";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var view = DocumentationUtils.GetElement<View>(doc, DocumentationUtils.ReadId(parameters, "viewId"))
                       ?? uiDoc.ActiveView;
            if (!view.AreGraphicsOverridesAllowed())
                return Fail($"View '{view.Name}' does not allow graphic overrides.");

            var reset = parameters.Value<bool?>("reset") ?? false;
            var overrides = parameters["overrides"] as JObject;
            if (!reset && (overrides == null || overrides.Count == 0))
                return Fail("Provide 'overrides' or set 'reset' to true.");

            var settings = reset ? new OverrideGraphicSettings() : GraphicOverrideBuilder.Build(doc, overrides);
            var visible = reset ? true : overrides?.Value<bool?>("visible");

            var targets = new JArray();
            if (parameters["elementIds"] is JArray ids)
                foreach (var id in ids.Select(t => t.Value<long>()).Distinct())
                    targets.Add(new JObject { ["kind"] = "element", ["id"] = id });
            if (parameters["categories"] is JArray categories)
                foreach (var name in categories.Select(t => t.ToString()).Distinct(StringComparer.OrdinalIgnoreCase))
                    targets.Add(new JObject { ["kind"] = "category", ["name"] = name });
            if (targets.Count == 0)
                return Fail("Provide 'elementIds' or 'categories'.");

            var results = DocumentationUtils.RunBatch(doc, "MCP: Override Graphics", targets, target =>
                target.Value<string>("kind") == "element"
                    ? ApplyToElement(doc, view, target.Value<long>("id"), settings, visible)
                    : ApplyToCategory(doc, view, target.Value<string>("name"), settings, visible));

            return Ok($"Applied overrides to {results.Count(r => r.Value<bool>("success"))} of {results.Count} targets in '{view.Name}'.",
                new
                {
                    viewId = view.Id.GetValue(),
                    summary = DocumentationUtils.Summarize(results)
                });
        }

        private static object ApplyToElement(Document doc, View view, long id, OverrideGraphicSettings settings, bool? visible)
        {
            var element = doc.GetElement(id.ToRevitElementId());
            if (element == null || element is ElementType)
                throw new ArgumentException($"Element {id} is not an element instance.");

            view.SetElementOverrides(element.Id, settings);

            if (visible == false)
            {
                if (!element.CanBeHidden(view))
                    throw new InvalidOperationException($"Element {id} cannot be hidden in this view.");
                view.HideElements(new List<ElementId> { element.Id });
            }
            else if (visible == true && element.IsHidden(view))
            {
                view.UnhideElements(new List<ElementId> { element.Id });
            }

            return new { kind = "element", elementId = id };
        }

        private static object ApplyToCategory(Document doc, View view, string name, OverrideGraphicSettings settings, bool? visible)
        {
            var category = DocumentationUtils.ResolveCategory(doc, name)
                           ?? throw new ArgumentException($"Category '{name}' not found.");
            if (!view.IsCategoryOverridable(category.Id))
                throw new InvalidOperationException($"Category '{category.Name}' cannot be overridden in this view.");

            view.SetCategoryOverrides(category.Id, settings);

            if (visible != null)
            {
                if (!view.CanCategoryBeHidden(category.Id))
                    throw new InvalidOperationException($"Category '{category.Name}' visibility cannot be changed in this view.");
                view.SetCategoryHidden(category.Id, !visible.Value);
            }

            return new { kind = "category", category = category.Name, categoryId = category.Id.GetValue() };
        }
    }
}
