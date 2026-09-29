using System;
using System.Collections.Generic;
using System.Linq;

namespace RegisterGeometry
{
    /// <summary>
    ///     A connected wall group: one or more <see cref="WallLeg"/>s joined
    ///     by endpoint proximity or explicit grouping identity. Group
    ///     confidence and shape are computed by <see cref="WallTopology"/>.
    /// </summary>
    public sealed class WallGroup
    {
        public WallGroup(string groupId, IEnumerable<WallLeg> legs)
        {
            GroupId = groupId ?? throw new ArgumentNullException(nameof(groupId));
            Legs = legs?.ToList() ?? new List<WallLeg>();
            if (Legs.Count == 0)
            {
                throw new ArgumentException("WallGroup requires at least one leg.", nameof(legs));
            }
        }

        public string GroupId { get; }

        public IReadOnlyList<WallLeg> Legs { get; }

        /// <summary>
        ///     Optional human-readable label (e.g. "CW-1").
        /// </summary>
        public string? GroupLabel { get; set; }

        public double GroupingConfidence { get; set; } = 1.0;

        public string GroupingMethod { get; set; } = "explicit";

        public WallShape Shape { get; set; } = WallShape.Unknown;

        /// <summary>
        ///     Aggregate axis-aligned bounds over all legs.
        /// </summary>
        public Bounds AggregateBounds
        {
            get
            {
                var b = Bounds.Empty;
                foreach (var leg in Legs)
                {
                    b = b.Union(leg.Bounds);
                }
                return b;
            }
        }
    }
}
