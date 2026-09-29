using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RegisterGeometry;
using RevitMCPCommandSet.Models.DataExtraction.Register;
using RevitMCPCommandSet.Services.DataExtraction.Register.Beam;
using RevitMCPSDK.API.Interfaces;
using ElementIdAlias = RevitMCPCommandSet.Utils.ElementIdExtensions;
using GeometrySupportCategory = RegisterGeometry.SupportCategory;
using PlanSupportCategory = RevitMCPCommandSet.Models.DataExtraction.Register.SupportCategory;

namespace RevitMCPCommandSet.Services.DataExtraction.Register
{
    /// <summary>
    ///     External-event handler that produces the beam register envelope.
    ///     The handler is intentionally short: it parses options, validates
    ///     the active document, walks the (lightweight) beam identity pass,
    ///     paginates the result, and only opens the heavy Solid access path
    /// for the rows that actually ship.
    /// </summary>
    public class GetBeamRegisterDataEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        public BeamRegisterResponse ResultInfo { get; private set; }
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        private BeamRegisterExtractionOptions _options;
        private List<WarningEntry> _parseWarnings = new List<WarningEntry>();

        public void SetOptions(BeamRegisterExtractionOptions options)
        {
            _options = options ?? new BeamRegisterExtractionOptions();
            _parseWarnings.Clear();
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        public void SetParseWarnings(List<WarningEntry> warnings)
        {
            _parseWarnings = warnings ?? new List<WarningEntry>();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public string GetName() => "Get Beam Register Data";

        public void Execute(UIApplication app)
        {
            try
            {
                if (app == null)
                {
                    ResultInfo = BeamRegisterErrorResult.Envelope(
                        RegisterWarningFactory.NoActiveDocument(),
                        _parseWarnings);
                    return;
                }

                var validation = ActiveDocumentValidator.Validate(app, null);
                if (!validation.IsValid)
                {
                    ResultInfo = BeamRegisterErrorResult.Envelope(validation.Warnings, _parseWarnings);
                    return;
                }

                if (_options == null)
                {
                    _options = new BeamRegisterExtractionOptions();
                }

                Document host = validation.Document;
                var (hostDocumentKey, hostTitle) = RegisterKeyProvider.ResolveHost(host);

                var tolerance = new ToleranceSettings(
                    angularDegrees: _options.Tolerances?.AngularDegrees ?? 0.5,
                    intersectionMm: _options.Tolerances?.IntersectionMm ?? 5,
                    groupingMm: _options.Tolerances?.GroupingMm ?? 10,
                    supportSearchMm: _options.Tolerances?.SupportSearchMm ?? 500,
                    snapMm: _options.Tolerances?.SnapMm ?? 1);

                var phaseFilter = PhaseFilter.Resolve(host, validation.View, _options.PhaseId);
                var designFilter = DesignOptionFilter.Resolve(
                    host, validation.View, _options.DesignOptionPolicy ?? DesignOptionPolicy.Primary);
                var levels = new LevelScopeResolver(host);
                var resolvedLevels = levels.ResolveAll(_options.LevelIds, _options.LevelNames, _parseWarnings);

                // Grids are collected once for the whole model; the
                // associator is reused for every beam on the page.
                var gridCollection = new FilteredElementCollector(host).OfClass(typeof(Grid)).Cast<Grid>();
                var gridRegistry = BeamGridAssociator.FromDocument(
                    tolerance,
                    gridCollection,
                    axisAssignments: null);

                // Support candidates live in a model-wide spatial index.
                // Their planar bounds are derived from the geometry-element
                // bounding box; we never open the Solid unless the candidate
                // is later selected for a beam.
                var scope = new HostScopeResolver(host, hostDocumentKey);
                var supportResolver = new BeamSupportResolver(tolerance, BuildPrecedence(_options));
                BuildSupportCandidateIndex(host, scope, _options.IncludeLinkedModels ?? false, supportResolver, _parseWarnings);

                // Beam identity pass.
                var collector = new BeamCollectionHelper();
                var identityEntries = collector.Collect(
                    host, scope, _options.IncludeLinkedModels ?? false,
                    levels, phaseFilter, designFilter);

                // Sort identities deterministically.
                var keys = identityEntries
                    .Select(e => new IdentityKey(e.DocumentKey, e.UniqueId, string.Empty))
                    .ToList();
                var sortedKeys = IdentityPager.Sort(keys);

                // Cursor plumbing.
                string filterHash = FilterHashBuilder.ComputeBeamHash(_options);
                IdentityKey? afterKey = null;
                int previousPageNumber = 0;
                if (!string.IsNullOrEmpty(_options.Cursor))
                {
                    if (CursorValidator.TryDecode(
                        _options.Cursor, filterHash, hostDocumentKey, out var cursor, out var reason))
                    {
                        afterKey = new IdentityKey(hostDocumentKey, cursor.LastRecordKey, string.Empty);
                        previousPageNumber = cursor.PageNumber;
                    }
                    else if (!string.IsNullOrEmpty(reason))
                    {
                        _parseWarnings.Add(RegisterWarningFactory.Build(
                            WarningSeverity.Warning,
                            reason,
                            $"Stale cursor rejected: {reason}"));
                    }
                }

                var pageSize = _options.PageSize ?? 100;
                var pager = new IdentityPager(pageSize);
                var slice = pager.Select(sortedKeys, afterKey);
                var pageKeys = slice.Keys;

                // Build the entries-by-key lookup only for the page.
                var entriesByKey = identityEntries.ToDictionary(
                    e => $"{e.DocumentKey}:{e.UniqueId}",
                    e => e);

                var builder = new BeamRecordBuilder(
                    host,
                    _options,
                    tolerance,
                    supportResolver,
                    new BeamGridAssociator(tolerance, gridRegistry.Registry),
                    _options.ParameterMap != null
                        ? _options.ParameterMap.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value)
                        : new Dictionary<string, IReadOnlyList<string>>(),
                    _options.IncludeCantilevers ?? true,
                    _options.MinClearSpanConfidence ?? 0.5);

                var gridNamesByUid = gridCollection.ToDictionary(g => g.UniqueId, g => g.Name ?? string.Empty);
                var records = builder.BuildPage(pageKeys, entriesByKey, gridNamesByUid);

                var snapshot = RegisterSnapshotFactory.Build(
                    host,
                    hostDocumentKey,
                    host.Application.VersionNumber,
                    _options.CoordinateSystem ?? CoordinateSystem.Project,
                    null,
                    phaseFilter.State.PhaseId,
                    designFilter.State.DesignOptionId,
                    DateTime.UtcNow);
                var filterSummary = RegisterSnapshotFactory.BuildFilterSummary(
                    _options,
                    filterHash,
                    phaseFilter.State.PhaseId,
                    resolvedLevels.Select(l => ElementIdAlias.GetValue(l.Id).ToString()).ToList(),
                    resolvedLevels.Select(l => l.Name).ToList(),
                    validation.View?.Id != null ? ElementIdAlias.GetValue(validation.View.Id).ToString() : null);

                var pageInfo = RegisterPageInfoFactory.FromSlice(
                    slice,
                    hostDocumentKey,
                    filterHash,
                    nextPageNumber: previousPageNumber + 1,
                    totalEstimate: slice.TotalCount);

                ResultInfo = new BeamRegisterResponse
                {
                    SchemaVersion = "1.0",
                    Snapshot = snapshot,
                    Filters = filterSummary,
                    Records = records,
                    Warnings = _parseWarnings,
                    Page = pageInfo,
                };
            }
            catch (Exception ex)
            {
                ResultInfo = BeamRegisterErrorResult.Envelope(
                    RegisterWarningFactory.Build(
                        WarningSeverity.Error,
                        "beam_extraction_failed",
                        ex.Message),
                    _parseWarnings);
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        private static IReadOnlyDictionary<GeometrySupportCategory, int> BuildPrecedence(
            BeamRegisterExtractionOptions options)
        {
            var defaults = SupportCandidateRanker.DefaultPrecedence();
            if (options?.SupportCategoryPrecedence == null || options.SupportCategoryPrecedence.Count == 0)
            {
                return defaults;
            }
            var map = new Dictionary<GeometrySupportCategory, int>();
            foreach (var kv in defaults)
            {
                map[kv.Key] = kv.Value;
            }
            int rank = 0;
            foreach (var category in options.SupportCategoryPrecedence)
            {
                var geometryCategory = category switch
                {
                    PlanSupportCategory.Wall => GeometrySupportCategory.Wall,
                    PlanSupportCategory.Column => GeometrySupportCategory.Column,
                    PlanSupportCategory.Beam => GeometrySupportCategory.Beam,
                    PlanSupportCategory.Other => GeometrySupportCategory.Other,
                    _ => GeometrySupportCategory.Unknown,
                };
                if (!map.ContainsKey(geometryCategory))
                {
                    map[geometryCategory] = defaults.TryGetValue(geometryCategory, out var existing)
                        ? existing
                        : 999;
                }
                map[geometryCategory] = rank++;
            }
            return map;
        }

        private static void BuildSupportCandidateIndex(
            Document host,
            HostScopeResolver scope,
            bool includeLinkedModels,
            BeamSupportResolver resolver,
            List<WarningEntry> warnings)
        {
            void IndexCategory(Document document, BuiltInCategory category, GeometrySupportCategory mapped)
            {
                var elements = new FilteredElementCollector(document).OfCategory(category);
                foreach (var elem in elements)
                {
                    if (elem == null) continue;
                    try
                    {
                        var bbox = elem.get_BoundingBox(null);
                        if (bbox == null) continue;
                        var (minX, minY, maxX, maxY) = RegisterUnitConverter.ToPlanMm(bbox);
                        var bounds = new Bounds(minX, minY, maxX, maxY);
                        resolver.AddCandidate(
                            ElementIdAlias.GetValue(elem.Id),
                            mapped,
                            bounds,
                            elem.UniqueId,
                            TryReadMark(elem));
                    }
                    catch (InvalidOperationException)
                    {
                        // Some candidates cannot report geometry; skip silently.
                    }
                }
            }

            IndexCategory(host, BuiltInCategory.OST_Walls, GeometrySupportCategory.Wall);
            IndexCategory(host, BuiltInCategory.OST_StructuralColumns, GeometrySupportCategory.Column);
            IndexCategory(host, BuiltInCategory.OST_Columns, GeometrySupportCategory.Column);
            IndexCategory(host, BuiltInCategory.OST_StructuralFraming, GeometrySupportCategory.Beam);

            if (includeLinkedModels)
            {
                var links = scope.ResolveLinkedContexts(true);
                foreach (var link in links)
                {
                    try
                    {
                        IndexCategory(link.LinkDocument, BuiltInCategory.OST_Walls, GeometrySupportCategory.Wall);
                        IndexCategory(link.LinkDocument, BuiltInCategory.OST_StructuralColumns, GeometrySupportCategory.Column);
                        IndexCategory(link.LinkDocument, BuiltInCategory.OST_Columns, GeometrySupportCategory.Column);
                        IndexCategory(link.LinkDocument, BuiltInCategory.OST_StructuralFraming, GeometrySupportCategory.Beam);
                    }
                    catch (InvalidOperationException ex)
                    {
                        warnings.Add(RegisterWarningFactory.Build(
                            WarningSeverity.Warning,
                            "linked_candidate_index_failed",
                            ex.Message));
                    }
                }
            }
        }

        private static string TryReadMark(Element element)
        {
            try
            {
                var markParam = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                return markParam?.AsString();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>
    ///     Builds a minimal envelope from a fatal warning so the command
    ///     can return a structured response instead of throwing.
    /// </summary>
    internal static class BeamRegisterErrorResult
    {
        public static object FromException(RegisterRequestParser.RegisterParseException ex)
        {
            return new BeamRegisterResponse
            {
                SchemaVersion = "1.0",
                Warnings = new List<WarningEntry>
                {
                    RegisterWarningFactory.Build(
                        WarningSeverity.Error,
                        "beam_options_invalid",
                        ex.Message),
                },
                Records = new List<BeamRecord>(),
            };
        }

        public static BeamRegisterResponse Envelope(
            WarningEntry warning,
            IEnumerable<WarningEntry> extra = null)
        {
            var all = new List<WarningEntry>();
            if (warning != null) all.Add(warning);
            if (extra != null) all.AddRange(extra);
            return new BeamRegisterResponse
            {
                SchemaVersion = "1.0",
                Warnings = all,
                Records = new List<BeamRecord>(),
            };
        }

        public static BeamRegisterResponse Envelope(
            IEnumerable<WarningEntry> warnings,
            IEnumerable<WarningEntry> extra = null)
        {
            var all = new List<WarningEntry>();
            if (warnings != null) all.AddRange(warnings);
            if (extra != null) all.AddRange(extra);
            return new BeamRegisterResponse
            {
                SchemaVersion = "1.0",
                Warnings = all,
                Records = new List<BeamRecord>(),
            };
        }
    }
}