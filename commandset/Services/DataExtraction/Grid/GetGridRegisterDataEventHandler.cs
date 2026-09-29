using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction.Register;
using RevitMCPCommandSet.Services.DataExtraction.Register;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;
using RegisterGeometry;
using GridExtraction = RevitMCPCommandSet.Services.DataExtraction.GridExtraction;
using ElementIdAlias = RevitMCPCommandSet.Utils.ElementIdExtensions;
using RPhaseFilter = RevitMCPCommandSet.Services.DataExtraction.Register.PhaseFilter;

namespace RevitMCPCommandSet.Services.DataExtraction.GridExtraction
{
    /// <summary>
    ///     External-event handler that produces a <see cref="GridRegisterResponse"/>
    ///     for the host document (and optionally its loaded links) using the
    ///     register pipeline.
    /// </summary>
    public class GetGridRegisterDataEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private GridRegisterExtractionOptions _options;
        private string _expectedFilterHash;
        private string _hostDocumentKey;
        private string _revitVersion;

        public GridRegisterResponse ResultInfo { get; private set; }
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetParameters(
            GridRegisterExtractionOptions options,
            string expectedFilterHash,
            string hostDocumentKey,
            string revitVersion)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _expectedFilterHash = expectedFilterHash ?? throw new ArgumentNullException(nameof(expectedFilterHash));
            _hostDocumentKey = hostDocumentKey ?? throw new ArgumentNullException(nameof(hostDocumentKey));
            _revitVersion = revitVersion ?? string.Empty;
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                ResultInfo = ExecuteCore(app);
            }
            catch (Exception ex)
            {
                ResultInfo = new GridRegisterResponse
                {
                    SchemaVersion = CursorValidator.CurrentSchemaVersion,
                    Warnings = new List<WarningEntry>
                    {
                        RegisterWarningFactory.Build(
                            WarningSeverity.Error,
                            "grid_extraction_failed",
                            $"Grid extraction failed: {ex.Message}"),
                    },
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName() => "Get Grid Register Data";

        private GridRegisterResponse ExecuteCore(UIApplication app)
        {
            var warnings = new List<WarningEntry>();

            // 1. Active document + view validation.
            var docValidation = ActiveDocumentValidator.Validate(app, _options.ViewId);
            warnings.AddRange(docValidation.Warnings);
            if (docValidation.Document == null)
            {
                return new GridRegisterResponse
                {
                    SchemaVersion = CursorValidator.CurrentSchemaVersion,
                    Warnings = warnings,
                };
            }
            var document = docValidation.Document;
            var view = docValidation.View;
            var hostKey = string.IsNullOrEmpty(_hostDocumentKey)
                ? RegisterKeyProvider.HostDocumentKey(
                    SafePathName(document),
                    document.Title)
                : _hostDocumentKey;

            // 2. Scope/policy resolvers.
            var levelResolver = new LevelScopeResolver(document);
            var levels = levelResolver.ResolveAll(_options.LevelIds, _options.LevelNames, warnings);
            var phaseFilter = RPhaseFilter.Resolve(document, view, _options.PhaseId, warnings);
            var designOption = DesignOptionFilter.Resolve(document, view, _options.DesignOptionPolicy ?? DesignOptionPolicy.Primary, warnings);

            // 3. Coordinate frame.
            var coordinateContext = new RegisterCoordinateContext(_options.CoordinateSystem ?? CoordinateSystem.Project, document);
            var projectLocationName = coordinateContext.ResolveProjectLocationName(document);

            // 4. Host collector.
            var collector = new GridExtraction.GridCollector(
                document,
                hostKey,
                view,
                coordinateContext,
                levels,
                phaseFilter,
                designOption);
            var hostGrids = collector.CollectHost();

            // 6. Linked models when requested.
            IReadOnlyList<LinkedDocumentContext> links = Array.Empty<LinkedDocumentContext>();
            var resolver = new HostScopeResolver(document, hostKey);
            if (_options.IncludeLinkedModels == true)
            {
                links = resolver.ResolveLinkedContexts(includeLinkedModels: true);
            }
            var linkGrids = collector.CollectLinks(links);
            var allGrids = hostGrids.Concat(linkGrids).ToList();

            // 5. Build registry, apply explicit assignments, resolve origin.
            var tolerances = new ToleranceSettings(
                _options.Tolerances?.AngularDegrees ?? 0.5,
                _options.Tolerances?.IntersectionMm ?? 5,
                _options.Tolerances?.GroupingMm ?? 10,
                _options.Tolerances?.SupportSearchMm ?? 500,
                _options.Tolerances?.SnapMm ?? 1);

            var (registry, resolutions, resolvedOrigin, builderWarnings) =
                GridExtraction.GridRecordBuilder.BuildRegistry(
                    allGrids,
                    _options.AxisAssignments,
                    _options.OriginGridUniqueId,
                    _options.OriginGridName,
                    tolerances);
            warnings.AddRange(builderWarnings);

            // 7. Build records (signed coordinates + spacing).
            var records = GridExtraction.GridRecordBuilder.BuildRecords(registry, resolutions, resolvedOrigin, tolerances);

            // Surface per-record warnings into envelope-level aggregation.
            var perRecordWarnings = records
                .SelectMany(r => r.Provenance?.Warnings ?? Enumerable.Empty<WarningEntry>())
                .ToList();
            foreach (var w in perRecordWarnings)
            {
                warnings.Add(w);
            }

            // 8. Deterministic paging.
            var pager = new IdentityPager(_options.PageSize ?? RegisterRequestParser.DefaultPageSize);
            var filterHash = _expectedFilterHash;
            Cursor cursor = default;
            int cursorPageNumber = 0;
            IdentityKey? typedAfterKey = null;
            string cursorRejection = null;
            if (!string.IsNullOrEmpty(_options.Cursor))
            {
                if (CursorValidator.TryDecode(
                        _options.Cursor,
                        expectedFilterHash: filterHash,
                        expectedDocumentKey: hostKey,
                        out cursor,
                        out cursorRejection))
                {
                    cursorPageNumber = cursor.PageNumber;
                    var raw = cursor.LastRecordKey ?? string.Empty;
                    var delim = raw.LastIndexOf('|');
                    if (delim > 0)
                    {
                        typedAfterKey = new IdentityKey(hostKey, raw.Substring(0, delim), raw.Substring(delim + 1));
                    }
                }
                else
                {
                    warnings.Add(RegisterWarningFactory.Build(
                        WarningSeverity.Warning,
                        cursorRejection ?? "cursor_invalid",
                        $"Cursor was rejected: {cursorRejection}; resuming from the first page."));
                }
            }

            var identityKeys = records
                .Select(r => new IdentityKey(
                    documentKey: r.Provenance?.DocumentKey ?? hostKey,
                    elementKey: r.Provenance?.UniqueId ?? string.Empty,
                    discriminator: r.AxisFamily.ToString()))
                .ToList();
            var sortedKeys = IdentityPager.Sort(identityKeys);
            var pageSlice = pager.Select(sortedKeys, typedAfterKey);
            var keyLookup = new HashSet<IdentityKey>(pageSlice.Keys);
            var pagedRecords = records
                .Join(
                    pageSlice.Keys,
                    r => new IdentityKey(
                        r.Provenance?.DocumentKey ?? hostKey,
                        r.Provenance?.UniqueId ?? string.Empty,
                        r.AxisFamily.ToString()),
                    k => k,
                    (r, _) => r)
                .ToList();

            var snapshot = RegisterSnapshotFactory.Build(
                document,
                hostKey,
                _revitVersion,
                _options.CoordinateSystem ?? CoordinateSystem.Project,
                projectLocationName,
                phaseFilter.State.PhaseId,
                designOption.State.DesignOptionId,
                DateTime.UtcNow);

            var filterSummary = RegisterSnapshotFactory.BuildFilterSummary(
                _options,
                filterHash,
                phaseFilter.State.PhaseId,
                levels.Select(l => ElementIdAlias.GetValue(l.Id).ToString()).ToList(),
                levels.Select(l => l.Name).ToList(),
                _options.ViewId);

            var pageInfo = RegisterPageInfoFactory.FromSlice(
                pageSlice,
                hostKey,
                filterHash,
                nextPageNumber: cursorPageNumber + 1,
                totalEstimate: records.Count);

            return new GridRegisterResponse
            {
                SchemaVersion = CursorValidator.CurrentSchemaVersion,
                Snapshot = snapshot,
                Filters = filterSummary,
                Records = pagedRecords,
                Warnings = warnings,
                Page = pageInfo,
            };
        }

        private static string SafePathName(Document document)
        {
            try
            {
                return document.PathName;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}