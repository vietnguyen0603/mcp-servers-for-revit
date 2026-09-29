using System;

namespace RegisterGeometry
{
    /// <summary>
    ///     Mirrors <c>Models.DataExtraction.Register.WallShape</c> for the pure
    ///     geometry layer. Handlers map between the two enums at the boundary.
    /// </summary>
    public enum WallShape
    {
        Unknown = 0,
        Planar = 1,
        L = 2,
        UorT = 3,
        Box = 4,
        Polygon = 5,
    }

    /// <summary>
    ///     One physical wall leg. A leg is a single straight centreline with
    ///     a thickness; multi-leg cores are expressed by linking several
    ///     <see cref="WallLeg"/>s together in a <see cref="WallGroup"/>.
    /// </summary>
    public sealed class WallLeg
    {
        public WallLeg(
            string uniqueId,
            string mark,
            Segment centreline,
            double thicknessMm,
            WallShape shape = WallShape.Unknown)
        {
            UniqueId = uniqueId ?? throw new ArgumentNullException(nameof(uniqueId));
            Mark = mark ?? string.Empty;
            Centreline = centreline;
            ThicknessMm = thicknessMm;
            Shape = shape;
        }

        public string UniqueId { get; }

        public string Mark { get; set; }

        public Segment Centreline { get; }

        public double ThicknessMm { get; set; }

        /// <summary>
        ///     Resolved shape. May be set by the topology pass.
        /// </summary>
        public WallShape Shape { get; set; }

        public Bounds Bounds => Centreline.Bounds.Inflated(ThicknessMm * 0.5);

        public Vector2 Direction => Centreline.Tangent;

        /// <summary>
        ///     Wall axis-aligned footprint. Assumes the wall is thin compared
        ///     to its length.
        /// </summary>
        public (Point2 Min, Point2 Max) Footprint()
        {
            var b = Bounds;
            return (b.Min, b.Max);
        }
    }
}
