using System;
using System.Globalization;

namespace RegisterGeometry
{
    /// <summary>
    ///     Immutable 2D point in double precision. Mirrors the units used by
    ///     the register response (millimetres) but the struct itself is
    ///     unit-agnostic and the caller is responsible for tagging values.
    /// </summary>
    public readonly struct Point2 : IEquatable<Point2>
    {
        public static readonly Point2 Origin = new Point2(0, 0);

        public Point2(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }

        public double Y { get; }

        /// <summary>
        ///     Distance to <paramref name="other"/>.
        /// </summary>
        public double DistanceTo(Point2 other)
        {
            double dx = other.X - X;
            double dy = other.Y - Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        ///     Squared distance to <paramref name="other"/>. Useful when only a
        ///     comparison is required because it avoids the square root.
        /// </summary>
        public double DistanceSquaredTo(Point2 other)
        {
            double dx = other.X - X;
            double dy = other.Y - Y;
            return dx * dx + dy * dy;
        }

        /// <summary>
        ///     Translate this point by <paramref name="delta"/>.
        /// </summary>
        public Point2 Offset(Vector2 delta) => new Point2(X + delta.X, Y + delta.Y);

        /// <summary>
        ///     Linear interpolation in [0,1].
        /// </summary>
        public Point2 Lerp(Point2 other, double t)
        {
            return new Point2(X + (other.X - X) * t, Y + (other.Y - Y) * t);
        }

        public bool Equals(Point2 other) => X.Equals(other.X) && Y.Equals(other.Y);

        public override bool Equals(object? obj) => obj is Point2 other && Equals(other);

        public override int GetHashCode() => HashUtility.Combine(HashUtility.Of(X), HashUtility.Of(Y));

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "({0:G15}, {1:G15})", X, Y);

        public static bool operator ==(Point2 left, Point2 right) => left.Equals(right);

        public static bool operator !=(Point2 left, Point2 right) => !left.Equals(right);

        public static Point2 operator +(Point2 left, Vector2 right) => left.Offset(right);

        public static Point2 operator -(Point2 left, Vector2 right) => left.Offset(-right);
    }
}
