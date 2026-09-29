using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using RegisterGeometry;
using RevitMCPCommandSet.Models.DataExtraction.Register;
using RevitMCPCommandSet.Services.DataExtraction.Register;
using ElementIdAlias = RevitMCPCommandSet.Utils.ElementIdExtensions;
using GeometryAxisFamily = RegisterGeometry.AxisFamily;
using PlanAxisFamily = RevitMCPCommandSet.Models.DataExtraction.Register.AxisFamily;
using PlanWallShape = RevitMCPCommandSet.Models.DataExtraction.Register.WallShape;
using GeometryWallShape = RegisterGeometry.WallShape;

namespace RevitMCPCommandSet.Services.DataExtraction.ColumnWallExtraction
{
    /// <summary>
    ///     Builds the per-page <see cref="ColumnWallRecord"/>s from the
    ///     lightweight <see cref="ColumnWallCollector.ColumnWallIdentity"/>
    ///     rows. Geometry probing (location curve, planar bounds) happens
    ///     here so the model-wide pass stays cheap.
    /// </summary>
    /// <remarks>
    ///     Two kinds are produced:
    ///     <list type="bullet">
    ///         <item><description>One row per physical column instance
    ///             (structural or architectural). The row's
    ///             <see cref="ColumnWallRecord.Shape"/> is always
    ///             <see cref="PlanWallShape.Planar"/>; legs and groups apply
    ///             only to walls.</description></item>
    ///         <item><description>One row per physical wall leg, optionally
    ///             joined into a group by <see cref="ColumnWallGrouper"/>.
    ///             Group metadata is exposed through
    ///             <see cref="ColumnWallRecord.WallGroup"/>.</description></item>
    ///     </list>
    ///     All dimensions are computed in the chosen coordinate frame
    ///     (project or shared) using <see cref="RegisterCoordinateContext"/>.
    /// </remarks>
    public sealed class ColumnWallRecordBuilder
    {
        private readonly ToleranceSettings _tolerance;
        private readonly NearestGridFinder _finder;
        private readonly GridRegistry _gridRegistry;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _parameterMap;
        private readonly ColumnWallGrouper _grouper;

        /// <summary>
        ///     Snapshot produced by the builder for the supplied page of
        ///     identity rows. <see cref="WallLegResolutions"/> exposes the
        ///     pure-geometry leg objects so the caller can carry them into a
        ///     later phase (e.g. solid intersection) without re-probing.
        /// </summary>
        public sealed class PageBuildResult
        {
            public PageBuildResult(
                List<ColumnWallRecord> records,
                List<WallLegResolution> wallLegResolutions,
                List<WarningEntry> warnings)
            {
                Records = records ?? new List<ColumnWallRecord>();
                WallLegResolutions = wallLegResolutions ?? new List<WallLegResolution>();
                Warnings = warnings ?? new List<WarningEntry>();
            }

            public List<ColumnWallRecord> Records { get; }

            public List<WallLegResolution> WallLegResolutions { get; }

            public List<WarningEntry> Warnings { get; }
        }

        public ColumnWallRecordBuilder(
            ToleranceSettings tolerance,
            GridRegistry gridRegistry,
            IReadOnlyDictionary<string, IReadOnlyList<string>> parameterMap,
            ColumnWallGrouper grouper)
        {
            _tolerance = tolerance;
            _gridRegistry = gridRegistry ?? throw new ArgumentNullException(nameof(gridRegistry));
            _parameterMap = parameterMap;
            _grouper = grouper ?? throw new ArgumentNullException(nameof(grouper));
            _finder = new NearestGridFinder(tolerance);
        }

        /// <summary>
        ///     Build the records for the supplied identity keys. The caller is
        ///     responsible for selecting which identities belong on the page
        ///     (typically via <see cref="IdentityPager"/>).
        /// </summary>
        public PageBuildResult BuildPage(
            IReadOnlyList<IdentityKey> pageKeys,
            IReadOnlyDictionary<string, ColumnWallCollector.ColumnWallIdentity> identitiesByKey,
            IReadOnlyDictionary<string, WallLegResolution> wallLegsByUniqueId,
            Func<WallLegResolution, string> explicitGroupKey,
            string hostDocumentKey,
            string revitVersion,
            DateTime extractedAtUtc)
        {
            if (pageKeys == null) throw new ArgumentNullException(nameof(pageKeys));
            if (identitiesByKey == null) throw new ArgumentNullException(nameof(identitiesByKey));
            if (wallLegsByUniqueId == null) throw new ArgumentNullException(nameof(wallLegsByUniqueId));

            var warnings = new List<WarningEntry>();
            var records = new List<ColumnWallRecord>(pageKeys.Count);

            // Group wall legs only on the page (the model-wide pass already
            // gave us the lightweight pass). The grouper returns stable
            // group objects keyed by leg unique id.
            var pageLegs = pageKeys
                .Where(k => wallLegsByUniqueId.ContainsKey(k.ElementKey))
                .Select(k => wallLegsByUniqueId[k.ElementKey])
                .ToList();
            var groupResult = pageLegs.Count == 0
                ? new ColumnWallGrouper.Result(Array.Empty<WallGroup>(), new Dictionary<string, WallGroup>())
                : _grouper.Build(pageLegs, explicitGroupKey);

            foreach (var key in pageKeys)
            {
                if (!identitiesByKey.TryGetValue(BuildIdentityLookupKey(key), out var identity))
                {
                    // A key was selected for the page but the lightweight
                    // pass dropped it; skip silently to keep the page
                    // count honest.
                    continue;
                }
                var record = BuildSingleRecord(
                    identity,
                    hostDocumentKey,
                    groupResult,
                    wallLegsByUniqueId,
                    warnings);
                records.Add(record);
            }

            return new PageBuildResult(records, pageLegs, warnings);
        }

        private ColumnWallRecord BuildSingleRecord(
            ColumnWallCollector.ColumnWallIdentity identity,
            string hostDocumentKey,
            ColumnWallGrouper.Result groupResult,
            IReadOnlyDictionary<string, WallLegResolution> wallLegsByUniqueId,
            List<WarningEntry> warnings)
        {
            var element = identity.Element;
            var lookup = new ParameterLookup(element, _parameterMap);

            var record = new ColumnWallRecord
            {
                Mark = lookup.ResolveString("mark"),
                Shape = PlanWallShape.Planar,
                LegNumber = 1,
                LegCount = 1,
                RotationDeg = 0.0,
                Provenance = BuildProvenance(element, identity, hostDocumentKey),
            };

            // Columns: report as first-class records.
            // Walls: extract a centreline + thickness; fall back to bounding
            // box only when no location curve is available.
            switch (identity.Kind)
            {
                case ColumnWallCollector.ElementKind.StructuralColumn:
                case ColumnWallCollector.ElementKind.ArchitecturalColumn:
                    BuildColumnRecord(identity, record, warnings);
                    break;
                case ColumnWallCollector.ElementKind.WallLeg:
                    BuildWallLegRecord(identity, record, groupResult, wallLegsByUniqueId, warnings);
                    break;
            }

            // Nearest grids + signed face offsets. Skipped when no X/Y grid
            // family exists so the row stays honest about non-tabular
            // layouts.
            var planarCentre = new Point2(record.CentrePointMm.X, record.CentrePointMm.Y);
            AttachNearestGrids(planarCentre, record);

            record.DimensionsDerivation = new DerivationInfo
            {
                Method = "planar_bounds",
                Confidence = 1.0,
                Note = "Plan dimensions derived from bounding-box projection.",
            };

            return record;
        }

        private static ElementProvenance BuildProvenance(
            Element element,
            ColumnWallCollector.ColumnWallIdentity identity,
            string hostDocumentKey)
        {
            var familyInstance = element as FamilyInstance;
            var type = familyInstance?.Symbol;
            return new ElementProvenance
            {
                ElementId = identity.ElementId,
                UniqueId = identity.UniqueId,
                DocumentKey = identity.DocumentKey ?? hostDocumentKey,
                LinkInstanceUniqueId = identity.LinkInstanceUniqueId,
                LevelId = TryReadLevelId(element),
                LevelName = TryReadLevelName(element),
                PhaseId = TryReadPhaseId(element),
                DesignOptionId = TryReadDesignOptionId(element),
                Category = element.Category?.Name,
                FamilyName = type?.Family?.Name,
                TypeName = type?.Name,
            };
        }

        /// <summary>
        ///     Resolve the level-bearing parameter for a column or wall,
        ///     walking the documented order. Columns expose their host level
        ///     through <c>FAMILY_LEVEL_PARAM</c> / <c>SCHEDULE_LEVEL_PARAM</c>
        ///     (some families use <c>INSTANCE_REFERENCE_LEVEL_PARAM</c>); walls
        ///     expose their base constraint via <c>WALL_BASE_CONSTRAINT</c>.
        /// </summary>
        internal static Parameter ResolveLevelParameter(Element element)
        {
            if (element == null) return null;
            return element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                   ?? element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                   ?? element.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)
                   ?? element.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)
                   ?? element.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
        }

        private static string TryReadLevelId(Element element)
        {
            var p = ResolveLevelParameter(element);
            if (p == null || p.StorageType != StorageType.ElementId) return null;
            var id = p.AsElementId();
            if (id == null || id == ElementId.InvalidElementId) return null;
            return ElementIdAlias.GetValue(id).ToString(CultureInfo.InvariantCulture);
        }

        private static string TryReadLevelName(Element element)
        {
            var p = ResolveLevelParameter(element);
            if (p == null || p.StorageType != StorageType.ElementId) return null;
            var id = p.AsElementId();
            if (id == null || id == ElementId.InvalidElementId) return null;
            var level = element.Document.GetElement(id) as Level;
            return level?.Name;
        }

        private static string TryReadPhaseId(Element element)
        {
            var p = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
            if (p == null || p.StorageType != StorageType.ElementId) return null;
            var id = p.AsElementId();
            if (id == null || id == ElementId.InvalidElementId) return null;
            return ElementIdAlias.GetValue(id).ToString(CultureInfo.InvariantCulture);
        }

        private static string TryReadDesignOptionId(Element element)
        {
            var p = element.get_Parameter(BuiltInParameter.DESIGN_OPTION_ID);
            if (p == null || p.StorageType != StorageType.ElementId) return null;
            var id = p.AsElementId();
            if (id == null || id == ElementId.InvalidElementId) return null;
            return ElementIdAlias.GetValue(id).ToString(CultureInfo.InvariantCulture);
        }

        private void BuildColumnRecord(
            ColumnWallCollector.ColumnWallIdentity identity,
            ColumnWallRecord record,
            List<WarningEntry> warnings)
        {
            var element = identity.Element;
            record.RecordKind = ColumnWallRecordKind.Column;

            var probe = RegisterInstanceAdapter.Probe(element);
            var (bounds, minZmm, maxZmm) = RegisterInstanceAdapter.TryGetPlanarBounds(element);

            // Plan footprint / extents / centre.
            if (!bounds.IsEmpty)
            {
                record.ExtentsMm = new BoundingBox2D
                {
                    MinMm = new PlanPoint2D { X = bounds.Min.X, Y = bounds.Min.Y },
                    MaxMm = new PlanPoint2D { X = bounds.Max.X, Y = bounds.Max.Y },
                };
                record.CentrePointMm = new PlanPoint2D
                {
                    X = bounds.Centre.X,
                    Y = bounds.Centre.Y,
                };
                record.DxMm = bounds.Width;
                record.DyMm = bounds.Height;
            }

            // Rotation: derived from the family instance transform when
            // available. Falls back to 0 when the instance has no
            // transform (e.g. some non-FamilyInstance columns).
            if (probe.InstanceTransform != null)
            {
                record.RotationDeg = RotationDegreesFromTransform(probe.InstanceTransform);
            }

            // Vertical extents: column base/top offsets relative to the
            // resolved reference level. We fall back to absolute elevation
            // differences when no level id could be resolved.
            AttachVerticalExtents(record, identity, minZmm, maxZmm);

            record.Classification = identity.Kind == ColumnWallCollector.ElementKind.ArchitecturalColumn
                ? "AC"
                : "SC";
        }

        private void BuildWallLegRecord(
            ColumnWallCollector.ColumnWallIdentity identity,
            ColumnWallRecord record,
            ColumnWallGrouper.Result groupResult,
            IReadOnlyDictionary<string, WallLegResolution> wallLegsByUniqueId,
            List<WarningEntry> warnings)
        {
            var element = identity.Element;
            record.RecordKind = ColumnWallRecordKind.WallLeg;

            WallLeg wallLeg = null;
            if (wallLegsByUniqueId != null
                && wallLegsByUniqueId.TryGetValue(identity.UniqueId, out var resolution))
            {
                wallLeg = resolution?.Leg;
            }
            else
            {
                // The handler normally pre-resolves wall legs for the page;
                // fall back to a direct probe when the builder is used
                // standalone (e.g. in a focused test).
                try
                {
                    wallLeg = element is Wall wall
                        ? WallGeometryProbe.ExtractLeg(wall, _tolerance.SnapMm)
                        : null;
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

            if (wallLeg == null)
            {
                // Surface a degenerate planar record so the row still ships
                // with explicit warnings instead of being dropped.
                var (bounds, _, _) = RegisterInstanceAdapter.TryGetPlanarBounds(element);
                if (!bounds.IsEmpty)
                {
                    record.ExtentsMm = new BoundingBox2D
                    {
                        MinMm = new PlanPoint2D { X = bounds.Min.X, Y = bounds.Min.Y },
                        MaxMm = new PlanPoint2D { X = bounds.Max.X, Y = bounds.Max.Y },
                    };
                    record.CentrePointMm = new PlanPoint2D
                    {
                        X = bounds.Centre.X,
                        Y = bounds.Centre.Y,
                    };
                    record.DxMm = bounds.Width;
                    record.DyMm = bounds.Height;
                }
                record.Classification = "WL";
                record.Shape = PlanWallShape.Unknown;
                record.Note = "Wall geometry could not be converted to a centreline leg.";
                return;
            }

            // Plan footprint / extents / centre from the leg's bounds.
            var legBounds = wallLeg.Bounds;
            record.ExtentsMm = new BoundingBox2D
            {
                MinMm = new PlanPoint2D { X = legBounds.Min.X, Y = legBounds.Min.Y },
                MaxMm = new PlanPoint2D { X = legBounds.Max.X, Y = legBounds.Max.Y },
            };
            record.CentrePointMm = new PlanPoint2D
            {
                X = legBounds.Centre.X,
                Y = legBounds.Centre.Y,
            };
            record.DxMm = legBounds.Width;
            record.DyMm = legBounds.Height;

            // Wall rotation: tangent angle in degrees from local +X axis.
            var tangent = wallLeg.Direction;
            if (tangent.LengthSquared > 0)
            {
                var normalised = tangent.Normalized();
                record.RotationDeg = Math.Atan2(normalised.Y, normalised.X) * (180.0 / Math.PI);
            }
            record.Classification = "WL";

            // Attach the group metadata when this leg ended up in one.
            if (groupResult.LegAssignments.TryGetValue(identity.UniqueId, out var group))
            {
                int legIndex = 1;
                for (int i = 0; i < group.Legs.Count; i++)
                {
                    if (string.Equals(group.Legs[i].UniqueId, identity.UniqueId, StringComparison.Ordinal))
                    {
                        legIndex = i + 1;
                        break;
                    }
                }
                record.Shape = (PlanWallShape)group.Shape;
                record.LegNumber = legIndex;
                record.LegCount = group.Legs.Count;
                record.WallGroup = new WallGroupInfo
                {
                    GroupId = group.GroupId,
                    GroupLabel = group.GroupLabel,
                    Shape = (PlanWallShape)group.Shape,
                    LegNumber = legIndex,
                    LegCount = group.Legs.Count,
                    GroupingConfidence = group.GroupingConfidence,
                    GroupingMethod = group.GroupingMethod,
                };
            }
            else
            {
                record.Shape = PlanWallShape.Planar;
            }
        }

        private void AttachVerticalExtents(
            ColumnWallRecord record,
            ColumnWallCollector.ColumnWallIdentity identity,
            double minZmm,
            double maxZmm)
        {
            // Resolve the reference level elevation so the offset is
            // reported as the difference between the bounding-box Z
            // extents and the level. Missing level means we fall back to
            // absolute elevation (caller can still compare to project
            // datum).
            double? levelElevation = null;
            var p = ResolveLevelParameter(identity.Element);
            if (p != null && p.StorageType == StorageType.ElementId)
            {
                var id = p.AsElementId();
                if (id != null && id != ElementId.InvalidElementId)
                {
                    if (identity.Document.GetElement(id) is Level level)
                    {
                        levelElevation = RegisterUnitConverter.MmFromFeet(level.Elevation);
                        if (string.IsNullOrEmpty(record.LevelName))
                        {
                            record.LevelName = level.Name;
                        }
                        if (string.IsNullOrEmpty(record.Provenance?.LevelName))
                        {
                            record.Provenance.LevelName = level.Name;
                        }
                    }
                }
            }
            if (levelElevation.HasValue)
            {
                record.BaseOffsetMm = minZmm - levelElevation.Value;
                record.TopOffsetMm = maxZmm - levelElevation.Value;
            }
            else
            {
                record.BaseOffsetMm = minZmm;
                record.TopOffsetMm = maxZmm;
            }
        }

        private void AttachNearestGrids(Point2 centre, ColumnWallRecord record)
        {
            var xHit = _finder.Find(_gridRegistry, GeometryAxisFamily.X, centre);
            var yHit = _finder.Find(_gridRegistry, GeometryAxisFamily.Y, centre);

            if (xHit.IsHit)
            {
                record.NearestGridX = new GridReference
                {
                    GridUniqueId = xHit.Grid.UniqueId,
                    GridName = xHit.Grid.Name,
                    SignedOffsetMm = xHit.SignedOffsetMm,
                    DistanceMm = xHit.DistanceMm,
                };
                // Signed offset to the left footprint face: centre offset
                // minus half the X footprint extent.
                record.OffsetLeftMm = xHit.SignedOffsetMm - (record.DxMm * 0.5);
            }
            if (yHit.IsHit)
            {
                record.NearestGridY = new GridReference
                {
                    GridUniqueId = yHit.Grid.UniqueId,
                    GridName = yHit.Grid.Name,
                    SignedOffsetMm = yHit.SignedOffsetMm,
                    DistanceMm = yHit.DistanceMm,
                };
                record.OffsetBottomMm = yHit.SignedOffsetMm - (record.DyMm * 0.5);
            }
        }

        private static double RotationDegreesFromTransform(Transform t)
        {
            // Project the local X basis into plan and atan2 against the
            // host +X axis. Z basis is ignored because columns in plan are
            // identified by their top-down orientation.
            double x = t.BasisX.X;
            double y = t.BasisX.Y;
            if (Math.Abs(x) < 1e-12 && Math.Abs(y) < 1e-12) return 0.0;
            return Math.Atan2(y, x) * (180.0 / Math.PI);
        }

        /// <summary>
        ///     Build the composite lookup key used by the page dictionary.
        ///     Mirrors the discriminator pattern from the grid handler so
        ///     the page lookup agrees with the identity key.
        /// </summary>
        private static string BuildIdentityLookupKey(IdentityKey key)
        {
            return key.DocumentKey + "|" + key.ElementKey;
        }
    }
}
