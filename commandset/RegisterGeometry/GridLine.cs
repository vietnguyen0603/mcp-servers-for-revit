using System;

namespace RegisterGeometry
{
    /// <summary>
    ///     One physical grid as held by the pure geometry layer. The grid is
    ///     always a single planar curve (line or arc) and tagged with the
    ///     axis family resolved by the direction classifier.
    /// </summary>
    public sealed class GridLine
    {
        public GridLine(
            string uniqueId,
            string name,
            ICurve2 curve,
            AxisFamily family)
        {
            UniqueId = uniqueId ?? throw new ArgumentNullException(nameof(uniqueId));
            Name = name ?? string.Empty;
            Curve = curve ?? throw new ArgumentNullException(nameof(curve));
            Family = family;
        }

        public string UniqueId { get; }

        public string Name { get; }

        public ICurve2 Curve { get; }

        public AxisFamily Family { get; set; }

        /// <summary>
        ///     When true the grid is the configured origin (coordinate zero)
        ///     for its family.
        /// </summary>
        public bool IsOrigin { get; set; }

        /// <summary>
        ///     Optional explicit axis assignment overriding the clustered
        ///     family. When set, the clusterer result is ignored.
        /// </summary>
        public bool HasExplicitFamily { get; set; }
    }
}
