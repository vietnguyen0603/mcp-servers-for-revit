using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Datums
{
    /// <summary>
    ///     Renames levels, moves them (elevation in mm), sets the Building Story
    ///     flag and creates missing structural / floor plan views, all in one
    ///     undoable transaction with per-item results.
    /// </summary>
    public class ModifyLevelsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Modify Levels";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "levels");
            var results = DocumentationUtils.RunBatch(doc, "MCP: Modify Levels", items, item => Modify(doc, (JObject)item));
            return Ok($"Modified {results.Count(r => r.Value<bool>("success"))} of {results.Count} levels.",
                DocumentationUtils.Summarize(results));
        }

        private static object Modify(Document doc, JObject item)
        {
            var level = ResolveLevel(doc, item);
            var changed = new List<string>();
            var createdViews = new JArray();

            var newName = item.Value<string>("newName");
            if (!string.IsNullOrWhiteSpace(newName) && newName != level.Name)
            {
                level.Name = newName;
                changed.Add("name");
            }

            var elevation = item.Value<double?>("elevation");
            if (elevation.HasValue)
            {
                var feet = DocumentationUtils.MmToFeet(elevation.Value);
                if (Math.Abs(level.Elevation - feet) > 1e-9)
                {
                    level.Elevation = feet;
                    changed.Add("elevation");
                }
            }

            var isBuildingStory = item.Value<bool?>("isBuildingStory");
            if (isBuildingStory.HasValue)
            {
                var parameter = level.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY);
                if (parameter == null || parameter.IsReadOnly)
                    throw new ArgumentException("Building Story cannot be set on this level.");
                parameter.Set(isBuildingStory.Value ? 1 : 0);
                changed.Add("isBuildingStory");
            }

            if (item.Value<bool?>("structuralPlan") == true)
            {
                var view = EnsurePlan(doc, level, ViewFamily.StructuralPlan, ViewType.EngineeringPlan);
                if (view != null)
                    createdViews.Add(new JObject { ["id"] = view.Id.GetValue(), ["name"] = view.Name, ["type"] = "StructuralPlan" });
            }

            if (item.Value<bool?>("floorPlan") == true)
            {
                var view = EnsurePlan(doc, level, ViewFamily.FloorPlan, ViewType.FloorPlan);
                if (view != null)
                    createdViews.Add(new JObject { ["id"] = view.Id.GetValue(), ["name"] = view.Name, ["type"] = "FloorPlan" });
            }

            return new JObject
            {
                ["levelId"] = level.Id.GetValue(),
                ["name"] = level.Name,
                ["elevation"] = DocumentationUtils.FeetToMm(level.Elevation),
                ["changed"] = new JArray(changed),
                ["createdViews"] = createdViews
            };
        }

        internal static Level ResolveLevel(Document doc, JObject item)
        {
            var id = DocumentationUtils.ReadId(item, "levelId");
            if (id != null)
            {
                return doc.GetElement(id.Value.ToRevitElementId()) as Level
                       ?? throw new ArgumentException($"Element {id} is not a level.");
            }

            var name = item.Value<string>("name");
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Give 'levelId' or 'name'.");

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            return levels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal))
                   ?? levels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"Level '{name}' not found. Available: {string.Join(", ", levels.Select(l => l.Name))}.");
        }

        /// <summary>Creates a plan of the given family for the level unless one already exists; returns the new view or null.</summary>
        internal static ViewPlan EnsurePlan(Document doc, Level level, ViewFamily family, ViewType viewType)
        {
            var exists = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Any(v => !v.IsTemplate && v.ViewType == viewType && v.GenLevel != null && v.GenLevel.Id == level.Id);
            if (exists)
                return null;

            var viewFamilyType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                                     .FirstOrDefault(t => t.ViewFamily == family)
                                 ?? throw new ArgumentException($"No {family} view family type in the document.");
            return ViewPlan.Create(doc, viewFamilyType.Id, level.Id);
        }
    }
}
