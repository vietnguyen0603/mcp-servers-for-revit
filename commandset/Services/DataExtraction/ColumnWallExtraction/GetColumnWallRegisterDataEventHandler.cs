using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RegisterGeometry;
using RevitMCPCommandSet.Models.DataExtraction.Register;
using RevitMCPCommandSet.Services.DataExtraction.Register;
using RevitMCPCommandSet.Services.DataExtraction.Register.Beam;
using RevitMCPSDK.API.Interfaces;
using ElementIdAlias = RevitMCPCommandSet.Utils.ElementIdExtensions;
using RPhaseFilter = RevitMCPCommandSet.Services.DataExtraction.Register.PhaseFilter;
using RDesignOptionFilter = RevitMCPCommandSet.Services.DataExtraction.Register.DesignOptionFilter;

namespace RevitMCPCommandSet.Services.DataExtraction.ColumnWallExtraction
{
    /// <summary>
    ///     External-event handler that produces the column/wall register
    ///     envelope. The handler is intentionally compact: it parses
    ///     options, validates the active document, walks the (lightweight)
    ///     column/wall identity pass, paginates the result, and only opens
    ///     the heavy geometry probe path for the rows that actually ship.
    /// </summary>
    public class GetColumnWallRegisterDataEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        public ColumnWallRegisterResponse ResultInfo { get; private set; }
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        private ColumnWallRegisterExtractionOptions _options;
        private string _expectedFilterHash;
        private string _hostDocumentKey;
        private string _revitVersion;

        public void SetParameters(
            ColumnWallRegisterExtractionOptions options,
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

        public string GetName() => "Get Column Wall Register Data";

        public void Execute(UIApplication app)
        {
            try
            {
                ResultInfo = ExecuteCore(app);
            }
            catch (Exception ex)
            {
                ResultInfo = new ColumnWallRegisterResponse
                {
                    SchemaVersion = CursorValidator.CurrentSchemaVersion,
                    Warnings = new List<WarningEntry>
                    {
                        RegisterWarningFactory.Build(
                            WarningSeverity.Error,
                            "column_wall_extraction_failed",
                            $"Column/wall extraction failed: {ex.Message}"),
                    },
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        private ColumnWallRegisterResponse ExecuteCore(UIApplication app)
        {
            var warnings = new List<WarningEntry>();

            // 1. Active document + view validation.
            var validation = ActiveDocumentValidator.Validate(app, _options.ViewId);
            warnings.AddRange(validation.Warnings);
            if (validation.Document == null)
            {
                return new ColumnWallRegisterResponse
                {
                    SchemaVersion = CursorValidator.CurrentSchemaVersion,
                    Warnings = warnings,
                };
            }
            var document = validation.Document;
            var view = validation.View;
            var hostKey = string.IsNullOrEmpty(_hostDocumentKey)
                ? RegisterKeyProvider.HostDocumentKey(
                    SafePathName(document),
                    document.Title)
                : _hostDocumentKey;

            // 2. Resolvers.
            var levelResolver = new LevelScopeResolver(document);
            var levels = levelResolver.ResolveAll(_options.LevelIds, _options.LevelNames, warnings);
            var phaseFilter = RPhaseFilter.Resolve(document, view, _options.PhaseId, warnings);
            var designOption = RDesignOptionFilter.Resolve(
                document, view, _options.DesignOptionPolicy ?? DesignOptionPolicy.Primary, warnings);
            var coordinateContext = new RegisterCoordinateContext(
                _options.CoordinateSystem ?? CoordinateSystem.Project,
                document);
            var projectLocationName = coordinateContext.ResolveProjectLocationName(document);

            var tolerances = new ToleranceSettings(
                _options.Tolerances?.AngularDegrees ?? 0.5,
                _options.Tolerances?.IntersectionMm ?? 5,
                _options.Tolerances?.GroupingMm ?? 10,
                _options.Tolerances?.SupportSearchMm ?? 500,
                _options.Tolerances?.SnapMm ?? 1);

            // 3. Host collector + optional links.
            var collector = new ColumnWallCollector();
            var scope = new HostScopeResolver(document, hostKey);
            var identities = collector.Collect(
                document,
                scope,
                includeLinkedModels: _options.IncludeLinkedModels ?? false,
                includeArchitecturalColumns: _options.IncludeArchitecturalColumns ?? false,
                requestedLevelIds: _options.LevelIds,
                requestedLevelNames: _options.LevelNames,
                phaseFilter: phaseFilter,
                designFilter: designOption);

            // 4. Build the grid registry once for the whole model so every
            // page shares the same nearest-grid result for a given element.
            var gridAssociator = BeamGridAssociator.FromDocument(
                tolerances,
                new FilteredElementCollector(document).OfClass(typeof(Grid)).Cast<Grid>());

            // 5. Deterministic paging.
            var keys = identities
                .Select(e => new IdentityKey(
                    documentKey: e.DocumentKey ?? hostKey,
                    elementKey: e.UniqueId,
                    discriminator: DiscriminatorFor(e)))
                .ToList();
            var sortedKeys = IdentityPager.Sort(keys);

            var pager = new IdentityPager(_options.PageSize ?? RegisterRequestParser.DefaultPageSize);
            var filterHash = _expectedFilterHash;

            Cursor cursor = default;
            int cursorPageNumber = 0;
            IdentityKey? afterKey = null;
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
                        var elementKey = raw.Substring(0, delim);
                        var discriminator = raw.Substring(delim + 1);
                        // Restore the same DocumentKey used to build the
                        // identity keys above.
                        var documentKey = FindDocumentKeyForElement(identities, elementKey) ?? hostKey;
                        afterKey = new IdentityKey(documentKey, elementKey, discriminator);
                    }
                }
                else if (!string.IsNullOrEmpty(cursorRejection))
                {
                    warnings.Add(RegisterWarningFactory.Build(
                        WarningSeverity.Warning,
                        cursorRejection,
                        $"Cursor was rejected: {cursorRejection}; resuming from the first page."));
                }
            }

            var pageSlice = pager.Select(sortedKeys, afterKey);
            var identitiesByKey = identities.ToDictionary(
                e => (e.DocumentKey ?? hostKey) + "|" + e.UniqueId,
                e => e);

            // 6. Build the grouper + record builder with the resolved options.
            var grouper = new ColumnWallGrouper(
                tolerances,
                new ColumnWallGrouperOptions
                {
                    CorePrefixes = _options.CorePrefixes,
                });
            var parameterMap = _options.ParameterMap != null
                ? _options.ParameterMap.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<string>)kv.Value)
                : new Dictionary<string, IReadOnlyList<string>>();

            // Pre-resolve wall legs for the page so the grouper can run in
            // one pass and the record builder can reuse them.
            var wallLegsByUniqueId = new Dictionary<string, WallLegResolution>(StringComparer.Ordinal);
            foreach (var key in pageSlice.Keys)
            {
                var lookup = (key.DocumentKey ?? hostKey) + "|" + key.ElementKey;
                if (!identitiesByKey.TryGetValue(lookup, out var identity)) continue;
                if (identity.Kind != ColumnWallCollector.ElementKind.WallLeg) continue;
                if (identity.Element is not Wall wall) continue;
                try
                {
                    var leg = WallGeometryProbe.ExtractLeg(wall, tolerances.SnapMm);
                    var resolution = new WallLegResolution(
                        leg, identity.UniqueId, identity.ElementId)
                    {
                        ExplicitGroupKey = ResolveExplicitGroupKey(wall, identity),
                    };
                    wallLegsByUniqueId[identity.UniqueId] = resolution;
                }
                catch (InvalidOperationException ex)
                {
                    warnings.Add(RegisterWarningFactory.Build(
                        WarningSeverity.Warning,
                        "wall_leg_extraction_failed",
                        $"Wall leg extraction failed: {ex.Message}",
                        elementId: identity.UniqueId));
                }
            }

            var builder = new ColumnWallRecordBuilder(
                tolerances,
                gridAssociator.Registry,
                parameterMap,
                grouper);

            // 7. Build the page records.
            var buildResult = builder.BuildPage(
                pageSlice.Keys,
                identitiesByKey,
                wallLegsByUniqueId,
                explicitGroupKey: TryResolveExplicitGroupKey,
                hostDocumentKey: hostKey,
                revitVersion: _revitVersion,
                extractedAtUtc: DateTime.UtcNow);
            warnings.AddRange(buildResult.Warnings);

            // Surface per-record warnings into envelope-level aggregation.
            foreach (var record in buildResult.Records)
            {
                if (record.Provenance?.Warnings != null)
                {
                    foreach (var pw in record.Provenance.Warnings)
                    {
                        warnings.Add(pw);
                    }
                }
            }

            // 8. Snapshot, filter summary, page info.
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
                levels.Select(l => ElementIdAlias.GetValue(l.Id).ToString(CultureInfo.InvariantCulture)).ToList(),
                levels.Select(l => l.Name).ToList(),
                _options.ViewId);

            var pageInfo = RegisterPageInfoFactory.FromSlice(
                pageSlice,
                hostKey,
                filterHash,
                nextPageNumber: cursorPageNumber + 1,
                totalEstimate: identities.Count);

            return new ColumnWallRegisterResponse
            {
                SchemaVersion = CursorValidator.CurrentSchemaVersion,
                Snapshot = snapshot,
                Filters = filterSummary,
                Records = buildResult.Records,
                Warnings = warnings,
                Page = pageInfo,
            };
        }

        /// <summary>
        ///     Resolve a stable explicit-group key for a wall leg. Walls may
        ///     belong to a Revit model group (one identity per group) or to
        ///     an assembly. Walls that are not part of either are returned
        ///     with a null key so the grouper falls through to the
        ///     core-prefix or same-mark phases.
        /// </summary>
        private static string TryResolveExplicitGroupKey(WallLegResolution resolution)
        {
            return resolution.ExplicitGroupKey;
        }

        /// <summary>
        ///     Read the model-group / assembly identity off the Revit wall.
        ///     The document key is folded into the value so identical Revit
        ///     group ids in the host and a linked model never collide. A wall
        ///     in no group and no assembly returns null.
        /// </summary>
        private static string ResolveExplicitGroupKey(
            Wall wall,
            ColumnWallCollector.ColumnWallIdentity identity)
        {
            if (wall == null) return null;
            var documentScope = identity.DocumentKey ?? string.Empty;

            var groupId = wall.GroupId;
            if (groupId != null && groupId != ElementId.InvalidElementId)
            {
                return "group:" + documentScope + ":" +
                    ElementIdAlias.GetValue(groupId).ToString(CultureInfo.InvariantCulture);
            }

            var assemblyId = wall.AssemblyInstanceId;
            if (assemblyId != null && assemblyId != ElementId.InvalidElementId)
            {
                return "assembly:" + documentScope + ":" +
                    ElementIdAlias.GetValue(assemblyId).ToString(CultureInfo.InvariantCulture);
            }

            return null;
        }

        private static string DiscriminatorFor(ColumnWallCollector.ColumnWallIdentity identity)
        {
            return identity.Kind switch
            {
                ColumnWallCollector.ElementKind.WallLeg => "wall-leg",
                _ => "column",
            };
        }

        private static string FindDocumentKeyForElement(
            List<ColumnWallCollector.ColumnWallIdentity> identities,
            string uniqueId)
        {
            foreach (var identity in identities)
            {
                if (string.Equals(identity.UniqueId, uniqueId, StringComparison.Ordinal))
                {
                    return identity.DocumentKey;
                }
            }
            return null;
        }

        private static string SafePathName(Document document)
        {
            try
            {
                return document?.PathName;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
