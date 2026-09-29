using System;
using System.Collections.Generic;
using System.Linq;

namespace RegisterGeometry
{
    /// <summary>
    ///     Pure wall-grouping builder. Grouping proceeds in three deterministic
    ///     phases:
    ///     <list type="number">
    ///         <item>
    ///             <description>
    ///                 <b>Explicit identity</b>: legs sharing an explicit group
    ///                 key (Revit model group or assembly identity) form a
    ///                 single group.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <b>Mark plus connectivity</b>: legs sharing the same
    ///                 configured mark are merged when at least one endpoint
    ///                 connects within tolerance.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <b>Geometric clustering</b>: any remaining legs are
    ///                 joined by endpoint proximity with a confidence discount.
    ///             </description>
    ///         </item>
    ///     </list>
    ///     Legs that remain ungrouped are emitted as single-leg groups with
    ///     <see cref="WallGroup.GroupingConfidence"/> = 0.
    /// </summary>
    public sealed class WallGroupBuilder
    {
        private readonly ToleranceSettings _tolerance;

        public WallGroupBuilder(ToleranceSettings tolerance)
        {
            _tolerance = tolerance;
        }

        public IReadOnlyList<WallGroup> Build(IReadOnlyList<WallLeg> legs, Func<WallLeg, string?>? explicitGroupKey = null)
        {
            if (legs == null) throw new ArgumentNullException(nameof(legs));
            if (legs.Count == 0) return Array.Empty<WallGroup>();

            var groups = new List<WallGroup>();
            var assigned = new bool[legs.Count];

            // Phase 1: explicit identity
            if (explicitGroupKey != null)
            {
                var byKey = new Dictionary<string, List<WallLeg>>(StringComparer.Ordinal);
                for (int i = 0; i < legs.Count; i++)
                {
                    var key = explicitGroupKey(legs[i]);
                    if (string.IsNullOrEmpty(key)) continue;
                    if (!byKey.TryGetValue(key, out var list))
                    {
                        list = new List<WallLeg>();
                        byKey[key] = list;
                    }
                    list.Add(legs[i]);
                    assigned[i] = true;
                }
                foreach (var kv in byKey)
                {
                    var list = kv.Value;
                    var grp = new WallGroup($"explicit:{kv.Key}", list)
                    {
                        GroupingMethod = "explicit",
                        GroupingConfidence = 1.0,
                        Shape = WallTopology.FromLegs(list, _tolerance.GroupingMm).Shape,
                    };
                    AssignShape(grp);
                    groups.Add(grp);
                }
            }

            // Phase 2: mark + connectivity
            var byMark = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < legs.Count; i++)
            {
                if (assigned[i]) continue;
                if (string.IsNullOrEmpty(legs[i].Mark)) continue;
                if (!byMark.TryGetValue(legs[i].Mark, out var list))
                {
                    list = new List<int>();
                    byMark[legs[i].Mark] = list;
                }
                list.Add(i);
            }
            foreach (var kv in byMark)
            {
                var mark = kv.Key;
                var indices = kv.Value;
                if (indices.Count == 1)
                {
                    EmitSingle(legs, indices[0], "mark_isolated", 0.5, groups);
                    assigned[indices[0]] = true;
                    continue;
                }
                // Greedy cluster by endpoint proximity within the mark.
                var clusters = ClusterByEndpoint(indices, legs, _tolerance.GroupingMm);
                foreach (var cluster in clusters)
                {
                    var grpLegs = cluster.Select(idx => legs[idx]).ToList();
                    if (grpLegs.Count == 1)
                    {
                        EmitSingle(legs, cluster[0], "mark_isolated", 0.5, groups);
                    }
                    else
                    {
                        var grp = new WallGroup($"mark:{mark}:{grpLegs.Count}", grpLegs)
                        {
                            GroupingMethod = "mark_connectivity",
                            GroupingConfidence = 0.8,
                        };
                        AssignShape(grp);
                        groups.Add(grp);
                    }
                    foreach (var idx in cluster) assigned[idx] = true;
                }
            }

            // Phase 3: pure geometry
            for (int i = 0; i < legs.Count; i++)
            {
                if (assigned[i]) continue;
                EmitSingle(legs, i, "ungrouped", 0.0, groups);
                assigned[i] = true;
            }

            return groups;
        }

        private void EmitSingle(IReadOnlyList<WallLeg> legs, int index, string method, double confidence, List<WallGroup> sink)
        {
            var leg = legs[index];
            var grp = new WallGroup($"single:{leg.UniqueId}", new[] { leg })
            {
                GroupingMethod = method,
                GroupingConfidence = confidence,
                Shape = WallShape.Planar,
            };
            sink.Add(grp);
        }

        private void AssignShape(WallGroup group)
        {
            var topology = WallTopology.FromLegs(group.Legs, _tolerance.GroupingMm);
            group.Shape = topology.Shape;
            for (int i = 0; i < group.Legs.Count; i++)
            {
                group.Legs[i].Shape = topology.Shape;
            }
        }

        /// <summary>
        ///     Greedy endpoint-proximity clustering. Two legs join the same
        ///     cluster when any endpoint of one is within
        ///     <paramref name="toleranceMm"/> of any endpoint of the other.
        /// </summary>
        private static List<List<int>> ClusterByEndpoint(
            IReadOnlyList<int> indices,
            IReadOnlyList<WallLeg> legs,
            double toleranceMm)
        {
            var clusters = new List<List<int>>();
            foreach (var idx in indices)
            {
                bool placed = false;
                foreach (var cluster in clusters)
                {
                    foreach (var otherIdx in cluster)
                    {
                        if (SharesEndpoint(legs[idx], legs[otherIdx], toleranceMm))
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
    }
}
