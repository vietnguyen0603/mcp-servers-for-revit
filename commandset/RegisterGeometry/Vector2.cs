using System;
using System.Globalization;

namespace RegisterGeometry
{
    /// <summary>
    ///     Immutable 2D vector. Direction classification and support ranking
    ///     work with vectors rather than points so the same primitives can be
    ///     used for grid directions, beam axes, and outward face normals.
    /// </summary>
    public readonly struct Vector2 : IEquatable<Vector2>
    {
        public static readonly Vector2 Zero = new Vector2(0, 0);

        /// <summary>
        ///     Unit vector aligned with the global X axis.
        /// </summary>
        public static readonly Vector2 UnitX = new Vector2(1, 0);

        /// <summary>
        ///     Unit vector aligned with the global Y axis.
        /// </summary>
        public static readonly Vector2 UnitY = new Vector2(0, 1);

        public Vector2(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }

        public double Y { get; }

        public double Length => Math.Sqrt(X * X + Y * Y);

        public double LengthSquared => X * X + Y * Y;

        /// <summary>
        ///     Returns a unit-length copy, or <see cref="Zero"/> if the vector is
        ///     degenerate (length below <paramref name="epsilon"/>).
        /// </summary>
        public Vector2 Normalized(double epsilon = 1e-12)
        {
            double len = Length;
            if (len <= epsilon) return Zero;
            return new Vector2(X / len, Y / len);
        }

        public double Dot(Vector2 other) => X * other.X + Y * other.Y;

        /// <summary>
        ///     2D cross product magnitude (Z-component of the 3D cross product).
        ///     Positive when <paramref name="other"/> is counter-clockwise from
        ///     this vector.
        /// </summary>
        public double Cross(Vector2 other) => X * other.Y - Y * other.X;

        public Vector2 Reversed() => new Vector2(-X, -Y);

        public static Vector2 operator -(Vector2 v) => v.Reversed();

        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.X + b.X, a.Y + b.Y);

        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.X - b.X, a.Y - b.Y);

        public static Vector2 operator *(Vector2 v, double s) => new Vector2(v.X * s, v.Y * s);

        public static Vector2 operator *(double s, Vector2 v) => new Vector2(v.X * s, v.Y * s);

        public static bool operator ==(Vector2 left, Vector2 right) => left.Equals(right);

        public static bool operator !=(Vector2 left, Vector2 right) => !left.Equals(right);

        public bool Equals(Vector2 other) => X.Equals(other.X) && Y.Equals(other.Y);

        public override bool Equals(object? obj) => obj is Vector2 other && Equals(other);

        public override int GetHashCode() => HashUtility.Combine(HashUtility.Of(X), HashUtility.Of(Y));

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "({0:G15}, {1:G15})", X, Y);
    }
}
