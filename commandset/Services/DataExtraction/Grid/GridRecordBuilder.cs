using System;
using System.Collections.Generic;
using System.Linq;
using RegisterGeometry;
using RevitMCPCommandSet.Models.DataExtraction.Register;
using RevitMCPCommandSet.Services.DataExtraction.Register;
using RSegment = RegisterGeometry.Segment;
using GAxis = RegisterGeometry.AxisFamily;
using DAxis = RevitMCPCommandSet.Models.DataExtraction.Register.AxisFamily;
using DCurveKind = RevitMCPCommandSet.Models.DataExtraction.Register.CurveKind;
using GCurveKind = RegisterGeometry.CurveKind;

namespace RevitMCPCommandSet.Services.DataExtraction.GridExtraction
{
    /// <summary>
    ///     Pure (Revit-independent) helpers that convert collected
    ///     <see cref="GridResolution"/> values into
    ///     <see cref="GridRecord"/>s, applying axis-family overrides, origin
    ///     resolution, signed coordinates and spacing.
    /// </summary>
    /// <remarks>
    ///     The helpers are deterministic: given the same inputs in the same
    ///     order they always produce the same records and the same paging
    ///     order (lexicographic by document key, then unique id, then
    ///     family-order discriminator).
    /// </remarks>
    internal static class GridRecordBuilder
    {
        private const string ClusterMethod = "direction_cluster";
        private const string ExplicitMethod = "explicit_assignment";
        private const string NonScalarMethod = "non_scalar_grid";

        /// <summary>
        ///     Convert the collected grids into a registry of
        ///     <see cref="GridLine"/>s. Returns the registry, the original
        ///     element resolutions keyed by unique id, and the unique id of
        ///     the resolved origin (when one was requested and resolved).
        /// </summary>
        public static (
            GridRegistry Registry,
            IReadOnlyDictionary<string, GridResolution> Resolutions,
            string ResolvedOriginUniqueId,
            List<WarningEntry> Warnings)
            BuildRegistry(
                IReadOnlyList<GridResolution> grids,
                IReadOnlyDictionary<string, DAxis> explicitAssignments,
                string originGridUniqueId,
                string originGridName,
                ToleranceSettings tolerances)
        {
            var warnings = new List<WarningEntry>();
            var resolutions = new Dictionary<string, GridResolution>(StringComparer.Ordinal);
            var gridLines = new List<GridLine>();

            // Partition by curve kind: lines go through the clusterer;
            // arcs get their own Radial family so scalar coordinates cannot
            // silently leak into a wrong bucket.
            var directionPool = new List<Vector2>(grids.Count);
            var lineGrids = new List<(GridResolution Resolution, ICurve2 Curve)>(grids.Count);
            var arcGrids = new List<(GridResolution Resolution, ICurve2 Curve)>(grids.Count);

            foreach (var resolution in grids)
            {
                if (!resolutions.ContainsKey(resolution.UniqueId))
                {
                    resolutions[resolution.UniqueId] = resolution;
                }
                if (resolution.Curve is ArcCurve2)
                {
                    arcGrids.Add((resolution, resolution.Curve));
                }
                else if (resolution.Curve != null)
                {
                    lineGrids.Add((resolution, resolution.Curve));
                }
            }

            foreach (var pair in lineGrids)
            {
                directionPool.Add(pair.Curve.Tangent);
            }
            var clusters = DirectionClusterer.Cluster(directionPool, SafeTolerances(tolerances));
            var lineFamily = new Dictionary<int, GAxis>();
            foreach (var cluster in clusters)
            {
                GAxis family = cluster.Family;
                foreach (var memberIndex in cluster.Members)
                {
                    lineFamily[memberIndex] = family;
                }
            }

            var classifier = new DirectionClassifier(SafeTolerances(tolerances));
            for (int i = 0; i < lineGrids.Count; i++)
            {
                var (resolution, curve) = lineGrids[i];
                GAxis family = lineFamily.TryGetValue(i, out var f) ? f : GAxis.Unknown;
                if (family == GAxis.Unknown)
                {
                    family = classifier.Classify(curve.Tangent);
                }
                gridLines.Add(new GridLine(resolution.UniqueId, resolution.Name, curve, family));
            }
            foreach (var (resolution, curve) in arcGrids)
            {
                gridLines.Add(new GridLine(resolution.UniqueId, resolution.Name, curve, GAxis.Radial));
            }

            ApplyExplicitAssignments(gridLines, explicitAssignments, resolutions, warnings);
            var registry = new GridRegistry(gridLines);

            var resolvedOriginUniqueId = ResolveOrigin(
                registry,
                originGridUniqueId,
                originGridName,
                warnings);

            return (registry, resolutions, resolvedOriginUniqueId, warnings);
        }

        private static ToleranceSettings SafeTolerances(ToleranceSettings tolerances)
        {
            return tolerances.AngularDegrees > 0 ? tolerances : ToleranceSettings.Default;
        }

        private static void ApplyExplicitAssignments(
            IReadOnlyList<GridLine> gridLines,
            IReadOnlyDictionary<string, DAxis> explicitAssignments,
            IReadOnlyDictionary<string, GridResolution> resolutions,
            List<WarningEntry> warnings)
        {
            if (explicitAssignments == null || explicitAssignments.Count == 0) return;
            var byName = new Dictionary<string, List<GridLine>>(StringComparer.Ordinal);
            foreach (var grid in gridLines)
            {
                if (string.IsNullOrEmpty(grid.Name)) continue;
                if (!byName.TryGetValue(grid.Name, out var list))
                {
                    list = new List<GridLine>();
                    byName[grid.Name] = list;
                }
                list.Add(grid);
            }

            foreach (var assignment in explicitAssignments)
            {
                var key = assignment.Key;
                var family = assignment.Value;
                var matched = false;
                var matchedGrids = new List<GridLine>();
                if (gridLines.Any(g => string.Equals(g.UniqueId, key, StringComparison.Ordinal)))
                {
                    matchedGrids.AddRange(gridLines.Where(g => string.Equals(g.UniqueId, key, StringComparison.Ordinal)));
                    matched = true;
                }
                else if (byName.TryGetValue(key, out var list) && list.Count == 1)
                {
                    matchedGrids.Add(list[0]);
                    matched = true;
                }
                else if (byName.TryGetValue(key, out var ambiguous) && ambiguous.Count > 1)
                {
                    warnings.Add(RegisterWarningFactory.Build(
                        WarningSeverity.Warning,
                        "axis_assignment_name_ambiguous",
                        $"Explicit axis assignment for '{key}' matched {ambiguous.Count} grids; skipped.",
                        detail: new Dictionary<string, object>
                        {
                            ["gridKey"] = key,
                            ["matchedCount"] = ambiguous.Count,
                        }));
                    continue;
                }
                if (!matched)
                {
                    warnings.Add(RegisterWarningFactory.Build(
                        WarningSeverity.Warning,
                        "axis_assignment_grid_not_found",
                        $"Explicit axis assignment for '{key}' did not match any in-scope grid.",
                        detail: new Dictionary<string, object>
                        {
                            ["gridKey"] = key,
                            ["axisFamily"] = family.ToString(),
                        }));
                    continue;
                }
                foreach (var grid in matchedGrids)
                {
                    if (grid.HasExplicitFamily && grid.Family != (GAxis)family)
                    {
                        warnings.Add(RegisterWarningFactory.Build(
                            WarningSeverity.Warning,
                            "axis_assignment_conflict",
                            $"Grid '{grid.UniqueId}' was assigned to both '{grid.Family}' and '{family}'; keeping '{grid.Family}'.",
                            detail: new Dictionary<string, object>
                            {
                                ["gridKey"] = grid.UniqueId,
                                ["kept"] = grid.Family.ToString(),
                                ["rejected"] = family.ToString(),
                            }));
                        continue;
                    }
                    grid.Family = (GAxis)family;
                    grid.HasExplicitFamily = true;
                }
            }
        }

        private static string ResolveOrigin(
            GridRegistry registry,
            string originGridUniqueId,
            string originGridName,
            List<WarningEntry> warnings)
        {
            if (string.IsNullOrEmpty(originGridUniqueId) && string.IsNullOrEmpty(originGridName))
            {
                return null;
            }
            if (!string.IsNullOrEmpty(originGridUniqueId))
            {
                if (registry.TryGet(originGridUniqueId, out _))
                {
                    return originGridUniqueId;
                }
                warnings.Add(RegisterWarningFactory.MissingOrigin(originGridUniqueId, null));
                return null;
            }
            var nameMatches = registry.All()
                .Where(g => string.Equals(g.Name, originGridName, StringComparison.Ordinal))
                .ToList();
            if (nameMatches.Count == 0)
            {
                warnings.Add(RegisterWarningFactory.MissingOrigin(null, originGridName));
                return null;
            }
            if (nameMatches.Count > 1)
            {
                var ids = nameMatches.Select(g => g.UniqueId).ToList();
                warnings.Add(RegisterWarningFactory.AmbiguousOriginName(originGridName, ids));
                return null;
            }
            return nameMatches[0].UniqueId;
        }

        /// <summary>
        ///     Project every record's coordinate and spacing against the
        ///     resolved origin. Skew and radial records keep null scalar
        ///     coordinates and a non_scalar_grid warning per record.
        /// </summary>
        public static List<GridRecord> BuildRecords(
            GridRegistry registry,
            IReadOnlyDictionary<string, GridResolution> resolutions,
            string originUniqueId,
            ToleranceSettings tolerances)
        {
            var records = new List<GridRecord>();
            var aggregateWarnings = new List<WarningEntry>();

            foreach (var kv in registry.ByFamily.OrderBy(kv => (int)kv.Key))
            {
                var family = kv.Key;
                var familyGrids = OrderByCoordinate(kv.Value, family);
                GridLine previous = null;
                foreach (var grid in familyGrids)
                {
                    var record = new GridRecord
                    {
                        AxisFamily = (DAxis)family,
                        CurveKind = grid.Curve.Kind == GCurveKind.Arc ? DCurveKind.Arc : DCurveKind.Line,
                        Name = grid.Name,
                        IsOrigin = string.Equals(grid.UniqueId, originUniqueId, StringComparison.Ordinal),
                        AxisFamilyDerivation = new DerivationInfo
                        {
                            Method = grid.HasExplicitFamily ? ExplicitMethod : ClusterMethod,
                            Confidence = grid.HasExplicitFamily ? 1.0 : 0.8,
                            Note = grid.HasExplicitFamily
                                ? "Family provided by axisAssignments."
                                : "Family inferred from direction clustering.",
                        },
                    };
                    if (grid.Curve is RSegment seg)
                    {
                        record.StartPointMm = new PlanPoint2D { X = seg.Start.X, Y = seg.Start.Y };
                        record.EndPointMm = new PlanPoint2D { X = seg.End.X, Y = seg.End.Y };
                    }
                    else if (grid.Curve is ArcCurve2 arc)
                    {
                        record.StartPointMm = new PlanPoint2D { X = arc.Start.X, Y = arc.Start.Y };
                        record.EndPointMm = new PlanPoint2D { X = arc.End.X, Y = arc.End.Y };
                        record.ArcGeometry = new ArcGeometryDto
                        {
                            CenterMm = new PlanPoint2D { X = arc.Centre.X, Y = arc.Centre.Y },
                            RadiusMm = arc.Radius,
                            StartAngleDeg = arc.StartAngleDeg,
                            EndAngleDeg = arc.EndAngleDeg,
                            IsFullCircle = arc.IsFullCircle,
                        };
                    }

                    record.Direction = ComputeDirectionLabel((DAxis)family);

                    if (resolutions.TryGetValue(grid.UniqueId, out var resolution))
                    {
                        // Copy so we don't mutate the collector's per-resolution provenance.
                        record.Provenance = new ElementProvenance
                        {
                            ElementId = resolution.Provenance.ElementId,
                            UniqueId = resolution.Provenance.UniqueId,
                            DocumentKey = resolution.Provenance.DocumentKey,
                            LinkInstanceUniqueId = resolution.Provenance.LinkInstanceUniqueId,
                            LevelId = resolution.Provenance.LevelId,
                            LevelName = resolution.Provenance.LevelName,
                            PhaseId = resolution.Provenance.PhaseId,
                            DesignOptionId = resolution.Provenance.DesignOptionId,
                            Category = resolution.Provenance.Category,
                        };
                    }

                    if (family == GAxis.Skew || family == GAxis.Radial || grid.Curve is ArcCurve2)
                    {
                        var perRecord = RegisterWarningFactory.Build(
                            WarningSeverity.Info,
                            "non_scalar_grid",
                            $"Grid '{grid.Name}' has no scalar coordinate (family {family}); curve preserved.",
                            elementId: grid.UniqueId,
                            detail: new Dictionary<string, object>
                            {
                                ["method"] = NonScalarMethod,
                                ["family"] = family.ToString(),
                            });
                        aggregateWarnings.Add(perRecord);
                        record.Note = "Non-scalar grid; curve geometry preserved.";
                        record.Provenance ??= new ElementProvenance();
                        if (record.Provenance.Warnings == null)
                        {
                            record.Provenance.Warnings = new List<WarningEntry>();
                        }
                        record.Provenance.Warnings.Add(perRecord);
                    }
                    else if (family == GAxis.X || family == GAxis.Y)
                    {
                        var axisCoord = ScalarAxisCoordinate(grid, family);
                        record.CoordinateMm = axisCoord;
                        if (previous != null)
                        {
                            record.SpacingMm = axisCoord - ScalarAxisCoordinate(previous, family);
                        }
                    }

                    records.Add(record);
                    previous = grid;
                }
            }

            if (originUniqueId != null)
            {
                var originRecord = records.FirstOrDefault(r =>
                    string.Equals(r.Provenance?.UniqueId, originUniqueId, StringComparison.Ordinal));
                if (originRecord != null)
                {
                    originRecord.IsOrigin = true;
                    originRecord.CoordinateMm = 0.0;
                    originRecord.SpacingMm = null;
                }
            }

            // Surface per-record warnings into envelope-level aggregation.
            foreach (var r in records)
            {
                if (r.Provenance?.Warnings != null)
                {
                    aggregateWarnings.AddRange(r.Provenance.Warnings);
                }
            }
            return records;
        }

        private static IReadOnlyList<GridLine> OrderByCoordinate(
            IReadOnlyList<GridLine> grids,
            GAxis family)
        {
            if (family == GAxis.Skew || family == GAxis.Radial)
            {
                return grids;
            }
            Func<GridLine, double> selector = family == GAxis.X
                ? g => g.Curve.SamplePoint.X
                : g => g.Curve.SamplePoint.Y;
            return grids.OrderBy(selector).ToList();
        }

        private static double ScalarAxisCoordinate(GridLine grid, GAxis family)
        {
            return family == GAxis.X ? grid.Curve.SamplePoint.X : grid.Curve.SamplePoint.Y;
        }

        private static string ComputeDirectionLabel(DAxis family)
        {
            return family switch
            {
                DAxis.X => "X",
                DAxis.Y => "Y",
                DAxis.Skew => "SKEW",
                DAxis.Radial => "RADIAL",
                _ => "UNKNOWN",
            };
        }
    }
}