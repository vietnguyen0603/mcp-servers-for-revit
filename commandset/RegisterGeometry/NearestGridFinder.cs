using System;

namespace RegisterGeometry
{
    /// <summary>
    ///     Result of a nearest-grid lookup against a single family.
    /// </summary>
    public readonly struct NearestGridHit
    {
        public NearestGridHit(GridLine grid, double signedOffsetMm, double distanceMm)
        {
            Grid = grid;
            SignedOffsetMm = signedOffsetMm;
            DistanceMm = distanceMm;
        }

        public GridLine Grid { get; }

        /// <summary>
        ///     Signed offset along the family's primary axis, in millimetres.
        ///     For X-family this is the difference between the query X and the
        ///     grid X. For Y-family this is the difference along Y. Zero when
        ///     the query projects exactly onto the grid.
        /// </summary>
        public double SignedOffsetMm { get; }

        /// <summary>
        ///     Absolute planar distance from the query point to the grid
        ///     (millimetres).
        /// </summary>
        public double DistanceMm { get; }

        public bool IsHit => Grid != null;
    }

    /// <summary>
    ///     Finds the nearest grid (by line or arc) to a query point. For
    ///     orthogonal families (X/Y) the signed offset is the difference along
    ///     the family's primary axis; for skew and radial families the offset
    ///     is the planar distance to the curve's sample point.
    /// </summary>
    public sealed class NearestGridFinder
    {
        private readonly ToleranceSettings _tolerance;

        public NearestGridFinder(ToleranceSettings tolerance)
        {
            _tolerance = tolerance;
        }

        /// <summary>
        ///     Find the nearest grid belonging to <paramref name="family"/> to
        ///     <paramref name="query"/>. Returns <see cref="NearestGridHit.IsHit"/>
        ///     false when no grid in the family is within
        ///     <see cref="ToleranceSettings.IntersectionMm"/>.
        /// </summary>
        public NearestGridHit Find(GridRegistry registry, AxisFamily family, Point2 query)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            var candidates = registry.InFamily(family);
            if (candidates.Count == 0) return default;

            double bestDistance = double.PositiveInfinity;
            NearestGridHit best = default;

            foreach (var grid in candidates)
            {
                double signed = ComputeSignedOffset(grid.Curve, query, family);
                double distance = Math.Abs(signed);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = new NearestGridHit(grid, signed, distance);
                }
            }

            if (best.Grid == null) return default;
            if (best.DistanceMm > _tolerance.IntersectionMm) return default;
            return best;
        }

        private static double ComputeSignedOffset(ICurve2 curve, Point2 query, AxisFamily family)
        {
            switch (family)
            {
                case AxisFamily.X:
                    return query.X - curve.SamplePoint.X;
                case AxisFamily.Y:
                    return query.Y - curve.SamplePoint.Y;
                default:
                    // Skew / radial: fall back to planar distance to the sample point.
                    return curve.SamplePoint.DistanceTo(query);
            }
        }

        /// <summary>
        ///     Convenience: distance from <paramref name="query"/> to the curve
        ///     as a planar distance. Useful for grids with skew families.
        /// </summary>
        public double DistanceToCurve(ICurve2 curve, Point2 query)
        {
            if (curve is Segment segment)
            {
                return segment.Project(query).DistanceTo(query);
            }
            return curve.SamplePoint.DistanceTo(query);
        }
    }
}
