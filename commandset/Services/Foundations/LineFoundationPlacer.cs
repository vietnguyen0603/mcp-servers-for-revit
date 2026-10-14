using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Foundations
{
    /// <summary>
    ///     create_foundations kind:'line' (continuous grade beams / strip footings from line-based
    ///     Structural Foundation families). Direct creation does not work for these families
    ///     (Revit 2025: NewFamilyInstance(curve, ..., NonStructural) leaves the geometry at the origin
    ///     with no level/host, the Footing overload throws, the face overload rejects the level plane,
    ///     CopyElement has "no corresponding Work Plane"). The working route: copy an instance the user
    ///     placed by hand (Place on Work Plane, hosted on the level) with the view-to-view
    ///     CopyElements through a plan view of that level, then set the copy's location curve.
    ///     Copies are made in a pre-pass, per (seed, view) group, by doubling (log2 n CopyElements
    ///     calls); each item then takes one copy. Unused copies and copies of failed items are deleted.
    /// </summary>
    internal class LineFoundationPlacer
    {
        private const double CopyStepFeet = 1000 / 304.8;

        private readonly FoundationModelUtils.Context _ctx;
        private readonly Func<FoundationModelUtils.Context, JObject, FamilySymbol> _resolveSymbol;
        private readonly Dictionary<JToken, Prepared> _prepared = new Dictionary<JToken, Prepared>();
        private readonly List<ElementId> _discard = new List<ElementId>();
        private readonly Dictionary<long, View> _planViews = new Dictionary<long, View>();
        private readonly Dictionary<string, FamilyInstance> _seeds = new Dictionary<string, FamilyInstance>(StringComparer.OrdinalIgnoreCase);
        private List<FamilyInstance> _lineInstances;

        public LineFoundationPlacer(FoundationModelUtils.Context ctx,
            Func<FoundationModelUtils.Context, JObject, FamilySymbol> resolveSymbol)
        {
            _ctx = ctx;
            _resolveSymbol = resolveSymbol;
        }

        private class Prepared
        {
            public string Error;
            public Level Level;
            public double ExtraOffset;
            public FamilySymbol Target;
            public FamilyInstance Seed;
            public View SourceView;
            public View DestView;
            public ElementId CopyId;
            public readonly List<string> Warnings = new List<string>();
        }

        private static bool IsLine(JToken token) =>
            token is JObject item && string.Equals(item.Value<string>("kind"), "line", StringComparison.OrdinalIgnoreCase);

        /// <summary>Resolves every line item and makes its copy; per-item errors are kept for <see cref="Create" />.</summary>
        public void Prepare(JArray items)
        {
            var groups = new Dictionary<string, List<Prepared>>();
            foreach (var token in items.Where(IsLine))
            {
                var prep = new Prepared();
                _prepared[token] = prep;
                try
                {
                    Resolve((JObject)token, prep);
                    var key = prep.Seed.Id.GetValue() + "|" + prep.SourceView.Id.GetValue() + "|" + prep.DestView.Id.GetValue();
                    if (!groups.TryGetValue(key, out var list))
                        groups[key] = list = new List<Prepared>();
                    list.Add(prep);
                }
                catch (Exception ex)
                {
                    prep.Error = ex.Message;
                }
            }

            foreach (var group in groups.Values)
            {
                var first = group[0];
                try
                {
                    var copies = MakeCopies(first.Seed, first.SourceView, first.DestView, group.Count);
                    for (var i = 0; i < group.Count; i++)
                        group[i].CopyId = copies[i];
                }
                catch (Exception ex)
                {
                    foreach (var prep in group)
                        prep.Error = $"Copying seed {first.Seed.Id.GetValue()} in view '{first.DestView.Name}' failed: {ex.Message}";
                }
            }
        }

        /// <summary>Deletes copies that were not used or whose item failed.</summary>
        public void Cleanup()
        {
            var ids = _discard.Concat(_prepared.Values.Where(p => p.CopyId != null).Select(p => p.CopyId))
                .Where(id => _ctx.Doc.GetElement(id) != null).Distinct().ToList();
            if (ids.Count == 0) return;
            try
            {
                _ctx.Doc.Delete(ids);
            }
            catch
            {
                // Leftover copies stay at the seed (offset along X); nothing else to do.
            }
        }

        public JObject Create(JObject item)
        {
            if (!_prepared.TryGetValue(item, out var prep))
                throw new InvalidOperationException("Line item was not prepared.");
            if (prep.Error != null)
                throw new ArgumentException(prep.Error);

            var doc = _ctx.Doc;
            var copyId = prep.CopyId;
            prep.CopyId = null; // taken: Cleanup no longer deletes it unless the item fails
            try
            {
                var instance = doc.GetElement(copyId) as FamilyInstance
                               ?? throw new InvalidOperationException("The copy of the seed is missing.");
                var warnings = prep.Warnings;

                if (prep.Target != null && instance.Symbol.Id != prep.Target.Id)
                {
                    if (!string.Equals(instance.Symbol.FamilyName, prep.Target.FamilyName, StringComparison.OrdinalIgnoreCase))
                        warnings.Add($"Seed family '{instance.Symbol.FamilyName}' differs from '{prep.Target.FamilyName}'; changed the type anyway.");
                    instance.ChangeTypeId(prep.Target.Id);
                }

                if (!(instance.Location is LocationCurve location))
                    throw new InvalidOperationException("The copy has no location curve (the seed is not a line-based family).");
                var z = location.Curve.GetEndPoint(0).Z;
                var start = FoundationModelUtils.ReadXY(item["start"], z, "start");
                var end = FoundationModelUtils.ReadXY(item["end"], z, "end");
                if (start.DistanceTo(end) < doc.Application.ShortCurveTolerance)
                    throw new ArgumentException("start and end are (almost) the same point.");
                location.Curve = Line.CreateBound(start, end);

                var offsetMm = item.Value<double?>("offset");
                if (offsetMm.HasValue || Math.Abs(prep.ExtraOffset) > 1e-9)
                    SetHostOffset(instance, FoundationModelUtils.ToFeet(offsetMm ?? 0) + prep.ExtraOffset, warnings);

                FoundationModelUtils.SetMark(_ctx, instance, item, warnings);
                var comments = item.Value<string>("comments");
                if (comments != null)
                {
                    var parameter = instance.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (parameter == null || parameter.IsReadOnly || !parameter.Set(comments))
                        warnings.Add("Could not set Comments.");
                }

                var copyLevel = LevelOf(instance);
                if (copyLevel != null && copyLevel.Id != prep.Level.Id)
                    warnings.Add($"The copy is on level '{copyLevel.Name}', not '{prep.Level.Name}'.");
                _ctx.NeedsRegenerate = true;

                var symbol = instance.Symbol;
                var result = new JObject
                {
                    ["id"] = instance.Id.GetValue(),
                    ["seedId"] = prep.Seed.Id.GetValue(),
                    ["viewId"] = prep.DestView.Id.GetValue(),
                    ["typeId"] = symbol.Id.GetValue(),
                    ["familyName"] = symbol.FamilyName,
                    ["typeName"] = symbol.Name,
                    ["levelName"] = prep.Level.Name,
                    ["lengthMm"] = FoundationModelUtils.ToMm(start.DistanceTo(end))
                };
                var hostOffset = HostOffsetParameter(instance);
                if (hostOffset != null)
                    result["offsetMm"] = FoundationModelUtils.ToMm(hostOffset.AsDouble());
                FoundationModelUtils.AddWarnings(result, warnings);
                return result;
            }
            catch
            {
                _discard.Add(copyId);
                throw;
            }
        }

        private void Resolve(JObject item, Prepared prep)
        {
            var doc = _ctx.Doc;
            prep.Level = FoundationModelUtils.ResolveLevel(_ctx, item["level"], prep.Warnings, out prep.ExtraOffset);

            var typeId = DocumentationUtils.ReadId(item, "typeId");
            var familyName = item.Value<string>("familyName")?.Trim();
            var typeName = item.Value<string>("typeName")?.Trim();
            if (typeId != null || !string.IsNullOrWhiteSpace(typeName))
            {
                prep.Target = _resolveSymbol(_ctx, item);
                familyName = prep.Target.FamilyName;
            }

            var seedId = DocumentationUtils.ReadId(item, "seedElementId");
            if (seedId != null)
            {
                prep.Seed = doc.GetElement(seedId.Value.ToRevitElementId()) as FamilyInstance
                            ?? throw new ArgumentException($"seedElementId {seedId} is not a family instance.");
                if (!(prep.Seed.Location is LocationCurve))
                    throw new ArgumentException($"seedElementId {seedId} is not line-based (no location curve).");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(familyName))
                    throw new ArgumentException("Give typeId, familyName (+ typeName) or seedElementId.");
                prep.Seed = FindSeed(familyName, prep.Target, prep.Level);
            }

            var seedLevel = LevelOf(prep.Seed)
                            ?? throw new ArgumentException(
                                $"Seed {prep.Seed.Id.GetValue()} has no level/host (an instance created through the API that way is broken). {PlaceByHand(familyName ?? prep.Seed.Symbol.FamilyName, prep.Level)}");
            prep.DestView = PlanView(prep.Level);
            prep.SourceView = seedLevel.Id == prep.Level.Id ? prep.DestView : PlanView(seedLevel);
            if (seedLevel.Id != prep.Level.Id)
                prep.Warnings.Add($"Seed {prep.Seed.Id.GetValue()} is on level '{seedLevel.Name}'; copied from view '{prep.SourceView.Name}' to '{prep.DestView.Name}'.");
        }

        private static string PlaceByHand(string familyName, Level level) =>
            $"Place one '{familyName}' instance by hand on level '{level.Name}' (Structural Foundation: Wall/Strip or the family's tool, Placement = Place on Work Plane, Work Plane = Level : {level.Name}), then rerun (optionally with seedElementId).";

        /// <summary>An existing line-based instance of the family on the level: same type first, then any type, lowest id.</summary>
        private FamilyInstance FindSeed(string familyName, FamilySymbol target, Level level)
        {
            var key = familyName + "|" + (target?.Id.GetValue() ?? 0) + "|" + level.Id.GetValue();
            if (_seeds.TryGetValue(key, out var cached) && cached.IsValidObject)
                return cached;

            if (_lineInstances == null)
                _lineInstances = new FilteredElementCollector(_ctx.Doc).OfCategory(BuiltInCategory.OST_StructuralFoundation)
                    .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .Where(f => f.Location is LocationCurve)
                    .OrderBy(f => f.Id.GetValue()).ToList();

            var onLevel = _lineInstances
                .Where(f => f.IsValidObject && string.Equals(f.Symbol.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
                .Where(f => LevelOf(f)?.Id == level.Id)
                .ToList();
            if (onLevel.Count == 0)
            {
                var loaded = new FilteredElementCollector(_ctx.Doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .Any(s => string.Equals(s.FamilyName, familyName, StringComparison.OrdinalIgnoreCase));
                if (!loaded)
                    throw new ArgumentException($"Family '{familyName}' is not loaded (use load_family).");
                throw new ArgumentException(
                    $"No hand-placed '{familyName}' instance on level '{level.Name}' to copy (line-based foundations cannot be created directly through the API). {PlaceByHand(familyName, level)}");
            }

            var seed = (target != null ? onLevel.FirstOrDefault(f => f.Symbol.Id == target.Id) : null) ?? onLevel[0];
            _seeds[key] = seed;
            return seed;
        }

        /// <summary>Host level (work plane on a level), else LevelId, else the level parameters.</summary>
        private Level LevelOf(FamilyInstance instance)
        {
            if (instance.Host is Level hostLevel)
                return hostLevel;
            var doc = _ctx.Doc;
            if (instance.LevelId != null && instance.LevelId != ElementId.InvalidElementId && doc.GetElement(instance.LevelId) is Level level)
                return level;
            foreach (var id in new[]
                     {
                         BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, BuiltInParameter.FAMILY_LEVEL_PARAM,
                         BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM
                     })
            {
                var parameter = instance.get_Parameter(id);
                if (parameter != null && parameter.StorageType == StorageType.ElementId &&
                    doc.GetElement(parameter.AsElementId()) is Level byParameter)
                    return byParameter;
            }
            return null;
        }

        /// <summary>A non-template structural / floor plan whose associated level is the level.</summary>
        private View PlanView(Level level)
        {
            var key = level.Id.GetValue();
            if (_planViews.TryGetValue(key, out var cached) && cached.IsValidObject)
                return cached;
            var view = new FilteredElementCollector(_ctx.Doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                           .Where(v => !v.IsTemplate && v.GenLevel != null && v.GenLevel.Id == level.Id &&
                                       (v.ViewType == ViewType.EngineeringPlan || v.ViewType == ViewType.FloorPlan))
                           .OrderBy(v => v.ViewType == ViewType.EngineeringPlan ? 0 : 1)
                           .ThenBy(v => v.Id.GetValue())
                           .FirstOrDefault()
                       ?? throw new ArgumentException(
                           $"No floor/structural plan view of level '{level.Name}' to copy through; create one (create_view) and rerun.");
            _planViews[key] = view;
            return view;
        }

        /// <summary>
        ///     n copies of the seed in <paramref name="dest" />: one view-to-view copy of the seed, then doubling
        ///     (copy the copies so far) with distinct X shifts so no two instances coincide.
        /// </summary>
        private List<ElementId> MakeCopies(FamilyInstance seed, View source, View dest, int n)
        {
            var doc = _ctx.Doc;
            var options = new CopyPasteOptions();
            var copies = Copy(source, new List<ElementId> { seed.Id }, dest, 1, options);
            if (copies.Count != 1)
                throw new InvalidOperationException($"CopyElements returned {copies.Count} elements for one seed.");
            while (copies.Count < n)
            {
                doc.Regenerate();
                var batch = copies.Take(Math.Min(copies.Count, n - copies.Count)).ToList();
                var created = Copy(dest, batch, dest, copies.Count, options);
                if (created.Count != batch.Count)
                    throw new InvalidOperationException($"CopyElements returned {created.Count} elements for {batch.Count}.");
                copies.AddRange(created);
            }
            return copies;
        }

        private List<ElementId> Copy(View source, ICollection<ElementId> ids, View dest, int step, CopyPasteOptions options)
        {
            var transform = Transform.CreateTranslation(new XYZ(CopyStepFeet * step, 0, 0));
            var created = ElementTransformUtils.CopyElements(source, ids, dest, transform, options);
            return created.Where(id => _ctx.Doc.GetElement(id) is FamilyInstance).OrderBy(id => id.GetValue()).ToList();
        }

        private static Parameter HostOffsetParameter(FamilyInstance instance)
        {
            foreach (var id in new[]
                     {
                         BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, BuiltInParameter.INSTANCE_ELEVATION_PARAM,
                         BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM
                     })
            {
                var parameter = instance.get_Parameter(id);
                if (parameter != null && parameter.StorageType == StorageType.Double)
                    return parameter;
            }
            return null;
        }

        private static void SetHostOffset(FamilyInstance instance, double offsetFeet, List<string> warnings)
        {
            var parameter = HostOffsetParameter(instance);
            if (parameter == null || parameter.IsReadOnly || !parameter.Set(offsetFeet))
                warnings.Add("Could not set the offset from host; the copy keeps the seed's offset.");
        }
    }
}
