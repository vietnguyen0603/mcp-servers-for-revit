using System;
using System.Collections.Generic;
using System.Globalization;

namespace RegisterGeometry
{
    /// <summary>
    ///     Axis-aligned 2D bounding box. Empty boxes represent the degenerate
    ///     "no points" state and never expand when points are added.
    /// </summary>
    public readonly struct Bounds : IEquatable<Bounds>
    {
        /// <summary>
        ///     Empty bounds (no points). Adding the first point sets the
        ///     minimum and maximum to the point's coordinates.
        /// </summary>
        public static readonly Bounds Empty = new Bounds(
            double.PositiveInfinity, double.PositiveInfinity,
            double.NegativeInfinity, double.NegativeInfinity);

        public Bounds(double minX, double minY, double maxX, double maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        public double MinX { get; }

        public double MinY { get; }

        public double MaxX { get; }

        public double MaxY { get; }

        public bool IsEmpty => double.IsPositiveInfinity(MinX) || double.IsPositiveInfinity(MinY);

        public double Width => IsEmpty ? 0 : Math.Max(0, MaxX - MinX);

        public double Height => IsEmpty ? 0 : Math.Max(0, MaxY - MinY);

        public Point2 Min => new Point2(MinX, MinY);

        public Point2 Max => new Point2(MaxX, MaxY);

        public Point2 Centre
        {
            get
            {
                if (IsEmpty) return Point2.Origin;
                return new Point2((MinX + MaxX) * 0.5, (MinY + MaxY) * 0.5);
            }
        }

        /// <summary>
        ///     Create bounds that tightly enclose <paramref name="points"/>.
        /// </summary>
        public static Bounds FromPoints(IReadOnlyList<Point2> points)
        {
            if (points == null || points.Count == 0) return Empty;
            var b = new Bounds(points[0].X, points[0].Y, points[0].X, points[0].Y);
            for (int i = 1; i < points.Count; i++)
            {
                var p = points[i];
                b = b.ExpandedToInclude(p);
            }
            return b;
        }

        /// <summary>
        ///     Expand this box so it also contains <paramref name="point"/>.
        ///     Empty boxes adopt the point's coordinates.
        /// </summary>
        public Bounds ExpandedToInclude(Point2 point)
        {
            if (IsEmpty) return new Bounds(point.X, point.Y, point.X, point.Y);
            return new Bounds(
                Math.Min(MinX, point.X),
                Math.Min(MinY, point.Y),
                Math.Max(MaxX, point.X),
                Math.Max(MaxY, point.Y));
        }

        /// <summary>
        ///     Union with another box.
        /// </summary>
        public Bounds Union(Bounds other)
        {
            if (IsEmpty) return other;
            if (other.IsEmpty) return this;
            return new Bounds(
                Math.Min(MinX, other.MinX),
                Math.Min(MinY, other.MinY),
                Math.Max(MaxX, other.MaxX),
                Math.Max(MaxY, other.MaxY));
        }

        /// <summary>
        ///     True when <paramref name="point"/> lies within this box. Honours
        ///     <paramref name="epsilon"/> so callers can perform tolerance-aware
        ///     contains-checks.
        /// </summary>
        public bool Contains(Point2 point, double epsilon = 0)
        {
            if (IsEmpty) return false;
            return point.X >= MinX - epsilon && point.X <= MaxX + epsilon &&
                   point.Y >= MinY - epsilon && point.Y <= MaxY + epsilon;
        }

        /// <summary>
        ///     True when this box intersects <paramref name="other"/> within
        ///     <paramref name="epsilon"/>.
        /// </summary>
        public bool Intersects(Bounds other, double epsilon = 0)
        {
            if (IsEmpty || other.IsEmpty) return false;
            return !(other.MinX > MaxX + epsilon || other.MaxX < MinX - epsilon ||
                     other.MinY > MaxY + epsilon || other.MaxY < MinY - epsilon);
        }

        /// <summary>
        ///     Chebyshev (axis-aligned box) distance to <paramref name="point"/>.
        ///     Zero when the point lies inside the box.
        /// </summary>
        public double ChebyshevDistanceTo(Point2 point)
        {
            if (IsEmpty) return double.PositiveInfinity;
            double dx = 0;
            double dy = 0;
            if (point.X < MinX) dx = MinX - point.X;
            else if (point.X > MaxX) dx = point.X - MaxX;
            if (point.Y < MinY) dy = MinY - point.Y;
            else if (point.Y > MaxY) dy = point.Y - MaxY;
            return Math.Max(dx, dy);
        }

        /// <summary>
        ///     Inflate the box by <paramref name="padding"/> on each side.
        /// </summary>
        public Bounds Inflated(double padding)
        {
            if (IsEmpty) return this;
            return new Bounds(MinX - padding, MinY - padding, MaxX + padding, MaxY + padding);
        }

        public bool Equals(Bounds other) =>
            MinX.Equals(other.MinX) && MinY.Equals(other.MinY) &&
            MaxX.Equals(other.MaxX) && MaxY.Equals(other.MaxY);

        public override bool Equals(object? obj) => obj is Bounds other && Equals(other);

        public override int GetHashCode() => HashUtility.Combine(
            HashUtility.Of(MinX), HashUtility.Of(MinY),
            HashUtility.Of(MaxX), HashUtility.Of(MaxY));

        public override string ToString() =>
            string.Format(
                CultureInfo.InvariantCulture,
                "[{0:G15},{1:G15}] -> [{2:G15},{3:G15}]",
                MinX, MinY, MaxX, MaxY);
    }
}
