using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Assigns a view template to views, applies its properties once
    ///     without keeping the assignment, or removes the assigned template.
    /// </summary>
    public class ApplyViewTemplateEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Apply View Template";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "viewIds");
            var remove = parameters.Value<bool?>("remove") ?? false;
            var propertiesOnly = parameters.Value<bool?>("applyPropertiesOnly") ?? false;

            View template = null;
            if (!remove)
            {
                var templateId = DocumentationUtils.ReadId(parameters, "templateId");
                if (templateId == null)
                    return Fail("Provide 'templateId', or set 'remove' to true.");
                template = DocumentationUtils.GetElement<View>(doc, templateId);
                if (template == null || !template.IsTemplate)
                    return Fail($"templateId {templateId} is not a view template.");
            }

            var results = DocumentationUtils.RunBatch(doc, "MCP: Apply View Template", items, token =>
            {
                var id = token.Value<long>();
                var view = DocumentationUtils.GetElement<View>(doc, id)
                           ?? throw new ArgumentException($"{id} is not a view.");
                if (view.IsTemplate)
                    throw new ArgumentException($"'{view.Name}' is itself a view template.");

                if (remove)
                {
                    view.ViewTemplateId = ElementId.InvalidElementId;
                }
                else if (propertiesOnly)
                {
                    view.ApplyViewTemplateParameters(template);
                }
                else
                {
                    if (!view.IsValidViewTemplate(template.Id))
                        throw new InvalidOperationException(
                            $"Template '{template.Name}' is not valid for {view.ViewType} view '{view.Name}'.");
                    view.ViewTemplateId = template.Id;
                }

                return new
                {
                    viewId = id,
                    viewName = view.Name,
                    templateId = view.ViewTemplateId == ElementId.InvalidElementId ? (long?)null : view.ViewTemplateId.GetValue()
                };
            });

            var verb = remove ? "Removed templates from" : propertiesOnly ? "Applied template properties to" : "Assigned template to";
            return Ok($"{verb} {results.Count(r => r.Value<bool>("success"))} of {results.Count} views.",
                DocumentationUtils.Summarize(results));
        }
    }
}
