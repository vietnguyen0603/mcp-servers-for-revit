using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Creates callout views in plan, section, elevation and detail views.
    ///     The callout rectangle is given by two opposite model-space corners
    ///     (mm) lying in the parent view's plane.
    /// </summary>
    public class CreateCalloutEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Create Callout";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "callouts");
            var results = DocumentationUtils.RunBatch(doc, "MCP: Create Callouts", items, item => CreateCallout(doc, (JObject)item));
            return Ok($"Created {results.Count(r => r.Value<bool>("success"))} of {results.Count} callouts.",
                DocumentationUtils.Summarize(results));
        }

        private static object CreateCallout(Document doc, JObject item)
        {
            var parent = DocumentationUtils.GetElement<View>(doc, DocumentationUtils.ReadId(item, "parentViewId"))
                         ?? throw new ArgumentException("'parentViewId' does not refer to a view.");
            if (parent.IsTemplate)
                throw new ArgumentException("'parentViewId' must not be a view template.");

            var min = DocumentationUtils.ReadPointMm(item["min"])
                      ?? throw new ArgumentException("'min' is required (mm).");
            var max = DocumentationUtils.ReadPointMm(item["max"])
                      ?? throw new ArgumentException("'max' is required (mm).");

            var callout = ViewSection.CreateCallout(doc, parent.Id, ResolveCalloutType(doc, parent, item), min, max);

            var name = item.Value<string>("name");
            if (!string.IsNullOrWhiteSpace(name))
                callout.Name = name.Trim();

            var scale = item.Value<int?>("scale");
            if (scale is int s && s > 0 && callout.Scale != s)
                callout.Scale = s;

            var templateId = DocumentationUtils.ReadId(item, "viewTemplateId");
            if (templateId != null)
            {
                var template = DocumentationUtils.GetElement<View>(doc, templateId);
                if (template == null || !template.IsTemplate)
                    throw new ArgumentException($"viewTemplateId {templateId} is not a view template.");
                callout.ViewTemplateId = template.Id;
            }

            return new
            {
                viewId = callout.Id.GetValue(),
                name = callout.Name,
                viewType = callout.ViewType.ToString(),
                parentViewId = parent.Id.GetValue(),
                scale = callout.Scale
            };
        }

        /// <summary>
        ///     Explicit type when given; otherwise the parent's own type for plans
        ///     and the first Detail view family type for sections and elevations.
        /// </summary>
        private static ElementId ResolveCalloutType(Document doc, View parent, JObject item)
        {
            var requestedId = DocumentationUtils.ReadId(item, "viewFamilyTypeId");
            if (requestedId != null)
            {
                return DocumentationUtils.GetElement<ViewFamilyType>(doc, requestedId)?.Id
                       ?? throw new ArgumentException($"viewFamilyTypeId {requestedId} is not a view family type.");
            }

            switch (parent.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.EngineeringPlan:
                case ViewType.AreaPlan:
                    return parent.GetTypeId();
                case ViewType.Section:
                case ViewType.Elevation:
                case ViewType.Detail:
                    return CreateViewEventHandler.ResolveViewFamilyType(doc, new JObject(), ViewFamily.Detail);
                default:
                    throw new ArgumentException(
                        $"Callouts can be created in plan, section, elevation and detail views, not {parent.ViewType}.");
            }
        }
    }
}
