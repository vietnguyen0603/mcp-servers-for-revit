using System;
using System.Collections.Generic;
using System.Linq;
using RegisterGeometry;

namespace RevitMCPCommandSet.Services.DataExtraction.ColumnWallExtraction
{
    /// <summary>
    ///     Classifies and groups wall legs after the lightweight pass. The
    ///     grouping pipeline is a deterministic four-phase walk so identical
    ///     inputs always yield the same groups and the same per-leg order:
    ///     <list type="number">
    ///         <item><description><b>Explicit identity</b>: legs sharing the
    ///             Revit model-group or assembly identity form one group with
    ///             confidence 1.0.</description></item>
    ///         <item><description><b>Core-prefix group</b>: legs whose mark
    ///             matches a configured <see cref="ColumnWallGrouperOptions.CorePrefixes"/>
    ///             entry are routed together as a "core wall" group with
    ///             confidence 0.9. Two prefix groups may merge when their
    ///             endpoints connect within tolerance.</description></item>
    ///         <item><description><b>Same mark + spatial connectivity</b>:
    ///             remaining legs sharing the same non-prefix mark are merged
    ///             when at least one endpoint connects within
    ///             <see cref="ToleranceSettings.GroupingMm"/>. Confidence 0.8
    ///             for clusters, 0.5 for isolated same-mark legs.</description></item>
    ///         <item><description><b>Geometric fallback</b>: any legs still
    ///             ungrouped are emitted as single-leg groups with confidence
    ///             0.0; never merged solely by mark.</description></item>
    ///     </list>
    /// </summary>
    /// <remarks>
    ///     This grouper deliberately avoids a phase that merges legs purely on
    ///     the basis of shared mark without spatial evidence; the documented
    ///     contract forbids merging by mark alone, so the same-prefix groups
    ///     are still required to pass the connectivity pass.
    /// </remarks>
    public sealed class ColumnWallGrouper
    {
        private readonly ToleranceSettings _tolerance;
        private readonly ColumnWallGrouperOptions _options;

        /// <summary>
        ///     Result of a grouping pass. <see cref="LegAssignments"/> maps
        ///     every input leg to the <see cref="WallGroup"/> it ended up in;
        ///     <see cref="Groups"/> is the deterministic list of groups in
        ///     stable order.
        /// </summary>
        public sealed class Result
        {
            public Result(IReadOnlyList<WallGroup> groups, IReadOnlyDictionary<string, WallGroup> legAssignments)
            {
                Groups = groups ?? Array.Empty<WallGroup>();
                LegAssignments = legAssignments ?? new Dictionary<string, WallGroup>();
            }

            public IReadOnlyList<WallGroup> Groups { get; }

            public IReadOnlyDictionary<string, WallGroup> LegAssignments { get; }
        }

        public ColumnWallGrouper(ToleranceSettings tolerance, ColumnWallGrouperOptions options)
        {
            _tolerance = tolerance;
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        ///     Run the four-phase grouping pipeline. <paramref name="legs"/>
        ///     is the flat collection of legs collected by the model-wide
        ///     pass; <paramref name="explicitGroupKey"/> returns a stable key
        ///     per leg (e.g. Revit model-group id) or null when none applies.
        /// </summary>
        public Result Build(
            IReadOnlyList<WallLegResolution> legs,
            Func<WallLegResolution, string> explicitGroupKey)
        {
            if (legs == null) throw new ArgumentNullException(nameof(legs));
            if (legs.Count == 0)
            {
                return new Result(Array.Empty<WallGroup>(), new Dictionary<string, WallGroup>());
            }

            var groups = new List<WallGroup>();
            var assigned = new bool[legs.Count];
            var legAssignments = new Dictionary<string, WallGroup>(StringComparer.Ordinal);

            // Phase 1: explicit identity. Each unique key becomes a group.
            RunExplicitPhase(legs, explicitGroupKey, assigned, groups, legAssignments);

            // Phase 2: core-prefix grouping. Any leg whose mark starts with a
            // configured prefix (case-insensitive) and is not yet assigned
            // joins a prefix bucket; buckets merge when their endpoint
            // connectivity graph connects.
            RunCorePrefixPhase(legs, assigned, groups, legAssignments);

            // Phase 3: same-mark + connectivity, but never on its own. Same-
            // mark isolated legs are emitted as single groups; clusters
            // require at least one endpoint connection within tolerance.
            RunSameMarkPhase(legs, assigned, groups, legAssignments);

            // Phase 4: geometric fallback. Remaining legs emit single-leg
            // groups at confidence 0.0; the caller can still attempt pure
            // geometry clustering but this baseline keeps every leg on the
            // record list.
            RunUngroupedPhase(legs, assigned, groups, legAssignments);

            return new Result(groups, legAssignments);
        }

        private void RunExplicitPhase(
            IReadOnlyList<WallLegResolution> legs,
            Func<WallLegResolution, string> explicitGroupKey,
            bool[] assigned,
            List<WallGroup> sink,
            Dictionary<string, WallGroup> assignments)
        {
            if (explicitGroupKey == null) return;
            var byKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < legs.Count; i++)
            {
                var key = explicitGroupKey(legs[i]);
                if (string.IsNullOrEmpty(key)) continue;
                if (!byKey.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    byKey[key] = list;
                }
                list.Add(i);
            }
            foreach (var kv in byKey)
            {
                var indices = kv.Value;
                var grpLegs = indices.Select(idx => legs[idx].Leg).ToList();
                var grp = new WallGroup($"explicit:{kv.Key}", grpLegs)
                {
                    GroupingMethod = "explicit",
                    GroupingConfidence = 1.0,
                    Shape = WallTopology.FromLegs(grpLegs, _tolerance.GroupingMm).Shape,
                };
                ApplyShape(grp);
                sink.Add(grp);
                AssignLegs(legs, indices, assigned, assignments, grp);
            }
        }

        private void RunCorePrefixPhase(
            IReadOnlyList<WallLegResolution> legs,
            bool[] assigned,
            List<WallGroup> sink,
            Dictionary<string, WallGroup> assignments)
        {
            var prefixes = _options.CorePrefixes;
            if (prefixes == null || prefixes.Count == 0) return;

            // Bucket assigned-by-prefix legs by the prefix itself so we can
            // decide which buckets to merge.
            var bucketLegs = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < legs.Count; i++)
            {
                if (assigned[i]) continue;
                var mark = legs[i].Leg.Mark;
                if (string.IsNullOrEmpty(mark)) continue;
                var matchedPrefix = MatchPrefix(mark, prefixes);
                if (matchedPrefix == null) continue;
                if (!bucketLegs.TryGetValue(matchedPrefix, out var list))
                {
                    list = new List<int>();
                    bucketLegs[matchedPrefix] = list;
                }
                list.Add(i);
            }

            // Build connectivity clusters across all bucketed legs.
            var cluster = ClusterConnected(bucketLegs.SelectMany(kv => kv.Value).ToList(), legs);
            foreach (var clusterIndices in cluster)
            {
                if (clusterIndices.Count == 0) continue;
                var grpLegs = clusterIndices.Select(idx => legs[idx].Leg).ToList();
                // Group id is built from the originating prefix set so the
                // caller can identify the prefix source on the wire.
                var prefixSet = clusterIndices
                    .Select(idx => MatchPrefix(legs[idx].Leg.Mark, prefixes))
                    .Where(p => p != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var idLabel = prefixSet.Count == 0
                    ? "core"
                    : string.Join("+", prefixSet);
                var grp = new WallGroup($"core:{idLabel}:{grpLegs.Count}", grpLegs)
                {
                    GroupingMethod = "core_prefix",
                    GroupingConfidence = grpLegs.Count > 1 ? 0.9 : 0.7,
                    Shape = WallTopology.FromLegs(grpLegs, _tolerance.GroupingMm).Shape,
                };
                ApplyShape(grp);
                sink.Add(grp);
                AssignLegs(legs, clusterIndices, assigned, assignments, grp);
            }
        }

        private void RunSameMarkPhase(
            IReadOnlyList<WallLegResolution> legs,
            bool[] assigned,
            List<WallGroup> sink,
            Dictionary<string, WallGroup> assignments)
        {
            var byMark = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < legs.Count; i++)
            {
                if (assigned[i]) continue;
                if (string.IsNullOrEmpty(legs[i].Leg.Mark)) continue;
                if (!byMark.TryGetValue(legs[i].Leg.Mark, out var list))
                {
                    list = new List<int>();
                    byMark[legs[i].Leg.Mark] = list;
                }
                list.Add(i);
            }

            foreach (var kv in byMark)
            {
                var indices = kv.Value;
                if (indices.Count == 1)
                {
                    EmitSingle(legs, indices[0], "mark_isolated", 0.5, assigned, sink, assignments);
                    continue;
                }

                var clusters = ClusterConnected(indices, legs);
                foreach (var clusterIndices in clusters)
                {
                    if (clusterIndices.Count == 1)
                    {
                        EmitSingle(legs, clusterIndices[0], "mark_isolated", 0.5, assigned, sink, assignments);
                    }
                    else
                    {
                        var grpLegs = clusterIndices.Select(idx => legs[idx].Leg).ToList();
                        var grp = new WallGroup($"mark:{kv.Key}:{grpLegs.Count}", grpLegs)
                        {
                            GroupingMethod = "mark_connectivity",
                            GroupingConfidence = 0.8,
                            Shape = WallTopology.FromLegs(grpLegs, _tolerance.GroupingMm).Shape,
                        };
                        ApplyShape(grp);
                        sink.Add(grp);
                        AssignLegs(legs, clusterIndices, assigned, assignments, grp);
                    }
                }
            }
        }

        private void RunUngroupedPhase(
            IReadOnlyList<WallLegResolution> legs,
            bool[] assigned,
            List<WallGroup> sink,
            Dictionary<string, WallGroup> assignments)
        {
            for (int i = 0; i < legs.Count; i++)
            {
                if (assigned[i]) continue;
                EmitSingle(legs, i, "ungrouped", 0.0, assigned, sink, assignments);
            }
        }

        private void EmitSingle(
            IReadOnlyList<WallLegResolution> legs,
            int index,
            string method,
            double confidence,
            bool[] assigned,
            List<WallGroup> sink,
            Dictionary<string, WallGroup> assignments)
        {
            var leg = legs[index].Leg;
            var grp = new WallGroup($"single:{leg.UniqueId}", new[] { leg })
            {
                GroupingMethod = method,
                GroupingConfidence = confidence,
                Shape = WallShape.Planar,
            };
            sink.Add(grp);
            assigned[index] = true;
            assignments[leg.UniqueId] = grp;
        }

        private static void ApplyShape(WallGroup group)
        {
            var topology = WallTopology.FromLegs(group.Legs, 0.0);
            for (int i = 0; i < group.Legs.Count; i++)
            {
                group.Legs[i].Shape = topology.Shape;
            }
        }

        private static void AssignLegs(
            IReadOnlyList<WallLegResolution> legs,
            IReadOnlyList<int> indices,
            bool[] assigned,
            Dictionary<string, WallGroup> assignments,
            WallGroup grp)
        {
            foreach (var idx in indices)
            {
                assigned[idx] = true;
                assignments[legs[idx].Leg.UniqueId] = grp;
            }
        }

        /// <summary>
        ///     Greedy endpoint-proximity clustering. Two legs join the same
        ///     cluster when any endpoint of one is within the configured
        ///     grouping tolerance of any endpoint of the other.
        /// </summary>
        private List<List<int>> ClusterConnected(
            IReadOnlyList<int> indices,
            IReadOnlyList<WallLegResolution> legs)
        {
            var clusters = new List<List<int>>();
            foreach (var idx in indices)
            {
                bool placed = false;
                foreach (var cluster in clusters)
                {
                    foreach (var otherIdx in cluster)
                    {
                        if (SharesEndpoint(legs[idx].Leg, legs[otherIdx].Leg, _tolerance.GroupingMm))
                        {
                            cluster.Add(idx);
                            placed = true;
                            break;
                        }
                    }
                    if (placed) break;
                }
                if (!placed)
                {
                    clusters.Add(new List<int> { idx });
                }
            }
            return clusters;
        }

        private static bool SharesEndpoint(WallLeg a, WallLeg b, double toleranceMm)
        {
            return a.Centreline.Start.DistanceTo(b.Centreline.Start) <= toleranceMm
                || a.Centreline.Start.DistanceTo(b.Centreline.End) <= toleranceMm
                || a.Centreline.End.DistanceTo(b.Centreline.Start) <= toleranceMm
                || a.Centreline.End.DistanceTo(b.Centreline.End) <= toleranceMm;
        }

        /// <summary>
        ///     Return the first matching prefix (case-insensitive) when the
        ///     mark begins with any of the configured prefixes. Returns null
        ///     when the mark is empty or matches nothing.
        /// </summary>
        private static string MatchPrefix(string mark, IReadOnlyList<string> prefixes)
        {
            if (string.IsNullOrEmpty(mark)) return null;
            for (int i = 0; i < prefixes.Count; i++)
            {
                var prefix = prefixes[i];
                if (string.IsNullOrEmpty(prefix)) continue;
                if (mark.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return prefix;
                }
            }
            return null;
        }
    }

    /// <summary>
    ///     Pure (Revit-independent) container that pairs a <see cref="WallLeg"/>
    ///     with the original Revit element identity so the grouper can report
    ///     stable group ids and the builder can resolve provenance later.
    /// </summary>
    public sealed class WallLegResolution
    {
        public WallLegResolution(WallLeg leg, string elementUniqueId, long elementId)
        {
            Leg = leg ?? throw new ArgumentNullException(nameof(leg));
            ElementUniqueId = elementUniqueId ?? throw new ArgumentNullException(nameof(elementUniqueId));
            ElementId = elementId;
        }

        public WallLeg Leg { get; }
        public string ElementUniqueId { get; }
        public long ElementId { get; }

        /// <summary>
        ///     Stable key identifying the Revit model group or assembly the
        ///     source wall belongs to (e.g. <c>group:12345</c>), or null when
        ///     the wall is in neither. Resolved by the caller while the Revit
        ///     element is in hand, because the pure grouper has no document
        ///     access. Legs sharing this key form one group in phase 1.
        /// </summary>
        public string ExplicitGroupKey { get; set; }
    }

    /// <summary>
    ///     Pure options carried into the grouper. Kept separate from the wire
    ///     DTO so the geometry layer stays free of Newtonsoft references and
    ///     the command-set DTOs are free to evolve independently.
    /// </summary>
    public sealed class ColumnWallGrouperOptions
    {
        /// <summary>
        ///     Optional mark prefixes used to flag multi-leg core/shear walls.
        ///     Used as grouping evidence only; never the sole merge criterion.
        /// </summary>
        public IReadOnlyList<string> CorePrefixes { get; set; }
    }
}
