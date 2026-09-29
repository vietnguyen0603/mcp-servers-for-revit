using System;

namespace RegisterGeometry
{
    /// <summary>
    ///     Straight planar segment. Endpoints may be in either order; the
    ///     canonical form (used by sorting and direction classification) is
    ///     obtained via <see cref="Canonicalise"/>.
    /// </summary>
    public readonly struct Segment : ICurve2, IEquatable<Segment>
    {
        public Segment(Point2 start, Point2 end)
        {
            Start = start;
            End = end;
        }

        public Point2 Start { get; }

        public Point2 End { get; }

        public CurveKind Kind => CurveKind.Line;

        public Point2 SamplePoint => new Point2(
            (Start.X + End.X) * 0.5,
            (Start.Y + End.Y) * 0.5);

        public Vector2 Tangent
        {
            get
            {
                var d = new Vector2(End.X - Start.X, End.Y - Start.Y).Normalized();
                return d;
            }
        }

        public Bounds Bounds
        {
            get
            {
                double minX = Math.Min(Start.X, End.X);
                double minY = Math.Min(Start.Y, End.Y);
                double maxX = Math.Max(Start.X, End.X);
                double maxY = Math.Max(Start.Y, End.Y);
                return new Bounds(minX, minY, maxX, maxY);
            }
        }

        public double Length
        {
            get
            {
                double dx = End.X - Start.X;
                double dy = End.Y - Start.Y;
                return Math.Sqrt(dx * dx + dy * dy);
            }
        }

        /// <summary>
        ///     Canonical form with the endpoint whose projection on X+Y is
        ///     smaller coming first. This makes equality and direction
        ///     classification independent of how the source Revit curve
        ///     happens to be ordered.
        /// </summary>
        public Segment Canonicalise()
        {
            if (Start.X < End.X) return this;
            if (Start.X > End.X) return new Segment(End, Start);
            // tie-break on Y for determinism
            return Start.Y <= End.Y ? this : new Segment(End, Start);
        }

        public bool Equals(Segment other) =>
            Start.Equals(other.Start) && End.Equals(other.End);

        public override bool Equals(object? obj) => obj is Segment other && Equals(other);

        public override int GetHashCode() => HashUtility.Combine(
            HashUtility.Of(Start), HashUtility.Of(End));

        /// <summary>
        ///     Project <paramref name="point"/> onto the line supporting this
        ///     segment. Returns the projection parameter <c>t</c> in
        ///     [0,1] when the projection lies inside the segment, otherwise
        ///     clamped to the nearest endpoint.
        /// </summary>
        public Point2 Project(Point2 point)
        {
            double dx = End.X - Start.X;
            double dy = End.Y - Start.Y;
            double denom = dx * dx + dy * dy;
            if (denom <= 1e-18)
            {
                return Start;
            }
            double t = ((point.X - Start.X) * dx + (point.Y - Start.Y) * dy) / denom;
            if (t < 0) t = 0;
            else if (t > 1) t = 1;
            return new Point2(Start.X + t * dx, Start.Y + t * dy);
        }

        /// <summary>
        ///     Signed perpendicular distance from <paramref name="point"/> to
        ///     the infinite line supporting this segment, normalised by the
        ///     segment's direction. Positive is on the left of the directed
        ///     segment from <see cref="Start"/> to <see cref="End"/>.
        /// </summary>
        public double SignedDistance(Point2 point)
        {
            double dx = End.X - Start.X;
            double dy = End.Y - Start.Y;
            double denom = Math.Sqrt(dx * dx + dy * dy);
            if (denom <= 1e-18) return 0;
            // 2D cross product of direction and offset = dx*(py-sy) - dy*(px-sx).
            // Positive means the point lies to the left of the directed segment.
            return (dx * (point.Y - Start.Y) - dy * (point.X - Start.X)) / denom;
        }

        /// <summary>
        ///     Parallel offset of the segment by <paramref name="distance"/>
        ///     (positive is to the left of the directed segment).
        /// </summary>
        public Segment Offset(double distance)
        {
            double dx = End.X - Start.X;
            double dy = End.Y - Start.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= 1e-18) return this;
            double nx = -dy / len * distance;
            double ny = dx / len * distance;
            return new Segment(
                new Point2(Start.X + nx, Start.Y + ny),
                new Point2(End.X + nx, End.Y + ny));
        }
    }
}
