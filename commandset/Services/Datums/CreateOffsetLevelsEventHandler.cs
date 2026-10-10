using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Datums
{
    /// <summary>
    ///     Creates a level at a fixed offset from each source level (e.g. "2ND TOP PLATE"
    ///     1' below "2ND"), with the source's type and the same 2D/3D extents and
    ///     bubbles in every elevation/section view, all in one undoable transaction.
    /// </summary>
    public class CreateOffsetLevelsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Create Offset Levels";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "levels");
            var defaults = new Defaults
            {
                Offset = parameters.Value<double?>("offset") ?? -304.8,
                Suffix = parameters.Value<string>("suffix") ?? " TOP PLATE",
                IfExists = parameters.Value<string>("ifExists") ?? "skip",
                MatchExtents = parameters.Value<bool?>("matchExtents") ?? true,
                IsBuildingStory = parameters.Value<bool?>("isBuildingStory") ?? false,
                PlanViews = parameters.Value<string>("planViews") ?? "none"
            };
            var results = DocumentationUtils.RunBatch(doc, "MCP: Create Offset Levels", items,
                item => Create(doc, (JObject)item, defaults));
            return Ok($"Processed {results.Count(r => r.Value<bool>("success"))} of {results.Count} offset levels.",
                DocumentationUtils.Summarize(results));
        }

        private static object Create(Document doc, JObject item, Defaults defaults)
        {
            var source = ModifyLevelsEventHandler.ResolveLevel(doc, item);
            var name = item.Value<string>("newName") ?? source.Name + defaults.Suffix;
            var elevation = source.Elevation + DocumentationUtils.MmToFeet(item.Value<double?>("offset") ?? defaults.Offset);

            var existing = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal));
            if (existing != null && existing.Id == source.Id)
                throw new ArgumentException($"Offset level name '{name}' is the source level itself.");
            if (existing != null && defaults.IfExists == "error")
                throw new ArgumentException($"Level '{name}' already exists.");
            if (existing != null && defaults.IfExists == "skip")
                return Describe(existing, source, "skipped", 0, new JArray());

            var level = existing ?? Level.Create(doc, elevation);
            if (existing != null)
                level.Elevation = elevation;
            if (existing == null)
            {
                level.Name = name;
                if (level.GetTypeId() != source.GetTypeId())
                    level.ChangeTypeId(source.GetTypeId());
            }

            level.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY)?.Set(defaults.IsBuildingStory ? 1 : 0);
            var views = defaults.MatchExtents ? MatchExtents(doc, source, level) : 0;
            return Describe(level, source, existing == null ? "created" : "updated", views, CreatePlans(doc, level, defaults.PlanViews));
        }

        /// <summary>
        ///     Copies the source level's extents into the offset level in every elevation,
        ///     section and detail view. The 3D (model) extent is stored per view direction,
        ///     so it is set from every view, not just the first one.
        /// </summary>
        private static int MatchExtents(Document doc, Level source, Level target)
        {
            var dz = new XYZ(0, 0, target.Elevation - source.Elevation);
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && (v.ViewType == ViewType.Elevation || v.ViewType == ViewType.Section || v.ViewType == ViewType.Detail))
                .Where(v => source.CanBeVisibleInView(v) && target.CanBeVisibleInView(v))
                .ToList();

            var matched = 0;
            foreach (var view in views)
            {
                var model = source.GetCurvesInView(DatumExtentType.Model, view).FirstOrDefault();
                var specific = source.GetCurvesInView(DatumExtentType.ViewSpecific, view).FirstOrDefault();
                if (model == null || specific == null)
                    continue;

                target.SetCurveInView(DatumExtentType.Model, view, model.CreateTransformed(Transform.CreateTranslation(dz)));
                target.SetCurveInView(DatumExtentType.ViewSpecific, view, specific.CreateTransformed(Transform.CreateTranslation(dz)));
                foreach (var end in new[] { DatumEnds.End0, DatumEnds.End1 })
                {
                    target.SetDatumExtentType(end, view, source.GetDatumExtentTypeInView(end, view));
                    if (source.IsBubbleVisibleInView(end, view))
                        target.ShowBubbleInView(end, view);
                    else
                        target.HideBubbleInView(end, view);
                }

                matched++;
            }

            return matched;
        }

        private static JArray CreatePlans(Document doc, Level level, string planViews)
        {
            var created = new JArray();
            if (planViews == "structural" || planViews == "both")
                AddPlan(created, ModifyLevelsEventHandler.EnsurePlan(doc, level, ViewFamily.StructuralPlan, ViewType.EngineeringPlan), "StructuralPlan");
            if (planViews == "floor" || planViews == "both")
                AddPlan(created, ModifyLevelsEventHandler.EnsurePlan(doc, level, ViewFamily.FloorPlan, ViewType.FloorPlan), "FloorPlan");
            return created;
        }

        private static void AddPlan(JArray created, ViewPlan view, string type)
        {
            if (view != null)
                created.Add(new JObject { ["id"] = view.Id.GetValue(), ["name"] = view.Name, ["type"] = type });
        }

        private static object Describe(Level level, Level source, string action, int matchedViews, JArray createdViews)
        {
            return new JObject
            {
                ["action"] = action,
                ["levelId"] = level.Id.GetValue(),
                ["name"] = level.Name,
                ["elevation"] = DocumentationUtils.FeetToMm(level.Elevation),
                ["sourceLevelId"] = source.Id.GetValue(),
                ["sourceName"] = source.Name,
                ["offset"] = DocumentationUtils.FeetToMm(level.Elevation - source.Elevation),
                ["matchedViews"] = matchedViews,
                ["createdViews"] = createdViews
            };
        }

        private class Defaults
        {
            public double Offset;
            public string Suffix;
            public string IfExists;
            public bool MatchExtents;
            public bool IsBuildingStory;
            public string PlanViews;
        }
    }
}
