using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Sets the view range of plan views. Offsets are millimetres relative
    ///     to each plane's associated level. Level fields accept a level id or
    ///     one of Current, LevelAbove, LevelBelow and Unlimited.
    /// </summary>
    public class SetViewRangeEventHandler : JsonParameterEventHandler
    {
        private static readonly (PlanViewPlane plane, string offset, string level)[] Planes =
        {
            (PlanViewPlane.TopClipPlane, "topMm", "topLevelId"),
            (PlanViewPlane.CutPlane, "cutPlaneMm", null),
            (PlanViewPlane.BottomClipPlane, "bottomMm", "bottomLevelId"),
            (PlanViewPlane.ViewDepthPlane, "viewDepthMm", "viewDepthLevelId")
        };

        public override string GetName() => "Set View Range";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "views");
            var results = DocumentationUtils.RunBatch(doc, "MCP: Set View Range", items, item => SetRange(doc, (JObject)item));
            return Ok($"Updated {results.Count(r => r.Value<bool>("success"))} of {results.Count} view ranges.",
                DocumentationUtils.Summarize(results));
        }

        private static object SetRange(Document doc, JObject item)
        {
            var view = DocumentationUtils.GetElement<ViewPlan>(doc, DocumentationUtils.ReadId(item, "viewId"))
                       ?? throw new ArgumentException("'viewId' does not refer to a plan view.");
            if (view.IsTemplate)
                throw new ArgumentException("Set the view range on the template's plan views, not the template itself.");

            var template = doc.GetElement(view.ViewTemplateId) as View;
            if (template != null && !template.GetNonControlledTemplateParameterIds()
                    .Contains(new ElementId(BuiltInParameter.PLAN_VIEW_RANGE)))
                throw new InvalidOperationException(
                    $"View range of '{view.Name}' is controlled by view template '{template.Name}'.");

            var range = view.GetViewRange();
            foreach (var (plane, offsetKey, levelKey) in Planes)
            {
                if (levelKey != null && item[levelKey] != null && item[levelKey].Type != JTokenType.Null)
                    range.SetLevelId(plane, ResolveLevel(doc, item[levelKey], levelKey));
                if (item.Value<double?>(offsetKey) is double offset)
                    range.SetOffset(plane, DocumentationUtils.MmToFeet(offset));
            }

            view.SetViewRange(range);

            var applied = view.GetViewRange();
            return new
            {
                viewId = view.Id.GetValue(),
                name = view.Name,
                range = Planes.Select(p => new
                {
                    plane = p.plane.ToString(),
                    level = LevelName(doc, applied.GetLevelId(p.plane)),
                    offsetMm = DocumentationUtils.FeetToMm(applied.GetOffset(p.plane))
                }).ToList()
            };
        }

        private static ElementId ResolveLevel(Document doc, JToken token, string key)
        {
            if (token.Type == JTokenType.String)
            {
                switch (token.ToString().Trim().ToLowerInvariant())
                {
                    case "current": return PlanViewRange.Current;
                    case "levelabove": return PlanViewRange.LevelAbove;
                    case "levelbelow": return PlanViewRange.LevelBelow;
                    case "unlimited": return PlanViewRange.Unlimited;
                }

                if (!long.TryParse(token.ToString(), out _))
                    throw new ArgumentException($"'{key}' must be a level id or Current, LevelAbove, LevelBelow, Unlimited.");
            }

            var level = doc.GetElement(token.Value<long>().ToRevitElementId()) as Level
                        ?? throw new ArgumentException($"'{key}' {token} is not a level.");
            return level.Id;
        }

        private static string LevelName(Document doc, ElementId id)
        {
            if (id == PlanViewRange.Current) return "Current";
            if (id == PlanViewRange.LevelAbove) return "LevelAbove";
            if (id == PlanViewRange.LevelBelow) return "LevelBelow";
            if (id == PlanViewRange.Unlimited) return "Unlimited";
            return (doc.GetElement(id) as Level)?.Name ?? id.GetValue().ToString();
        }
    }
}
