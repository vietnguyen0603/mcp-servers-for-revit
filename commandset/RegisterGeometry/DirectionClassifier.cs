using System;

namespace RegisterGeometry
{
    /// <summary>
    ///     Mirrors <c>Models.DataExtraction.Register.AxisFamily</c> so the
    ///     geometry layer does not depend on the DTO assembly. Handlers map
    ///     between the two when serialising.
    /// </summary>
    public enum AxisFamily
    {
        Unknown = 0,
        X = 1,
        Y = 2,
        Skew = 3,
        Radial = 4,
    }

    /// <summary>
    ///     Classifies a unit vector into a canonical axis family given an
    ///     angular tolerance. The classifier is deterministic: equal inputs
    ///     (within tolerance) always produce the same family.
    /// </summary>
    public sealed class DirectionClassifier
    {
        private readonly double _toleranceRadians;

        public DirectionClassifier(ToleranceSettings tolerance)
        {
            _toleranceRadians = Math.Max(1e-6, tolerance.AngularDegrees * Math.PI / 180.0);
        }

        public DirectionClassifier(double angularDegrees)
        {
            _toleranceRadians = Math.Max(1e-6, angularDegrees * Math.PI / 180.0);
        }

        public double ToleranceRadians => _toleranceRadians;

        /// <summary>
        ///     Resolve the axis family for <paramref name="direction"/>. Zero
        ///     vectors return <see cref="AxisFamily.Unknown"/>.
        /// </summary>
        public AxisFamily Classify(Vector2 direction)
        {
            var unit = direction.Normalized();
            if (unit == Vector2.Zero) return AxisFamily.Unknown;

            // Normalise the polar angle to [0, π] so opposite directions
            // (±X or ±Y) collapse onto the same canonical axis.
            double ax = Math.Atan2(unit.Y, unit.X);
            double axAbs = Math.Abs(ax);
            if (axAbs > Math.PI) axAbs = 2 * Math.PI - axAbs;

            double fromX = Math.Min(axAbs, Math.PI - axAbs); // [0, π/2]
            double fromY = Math.Abs(axAbs - Math.PI / 2);    // [0, π/2]
            double minAxial = Math.Min(fromX, fromY);
            if (minAxial > _toleranceRadians) return AxisFamily.Skew;
            return fromX <= fromY ? AxisFamily.X : AxisFamily.Y;
        }

        /// <summary>
        ///     Returns the angle (in radians) between <paramref name="a"/> and
        ///     <paramref name="b"/>, normalised to [0, π/2]. This is what the
        ///     classifier compares against the angular tolerance.
        /// </summary>
        public static double AcuteAngle(Vector2 a, Vector2 b)
        {
            var ua = a.Normalized();
            var ub = b.Normalized();
            if (ua == Vector2.Zero || ub == Vector2.Zero) return Math.PI;
            double cos = Math.Max(-1.0, Math.Min(1.0, Math.Abs(ua.Dot(ub))));
            // Cap at 90° because opposite directions are treated as parallel.
            if (cos < 0) cos = -cos;
            return Math.Acos(cos);
        }

        /// <summary>
        ///     Returns the canonical representative direction for a family,
        ///     oriented consistently with <paramref name="direction"/> when
        ///     possible.
        /// </summary>
        public static Vector2 CanonicalDirection(AxisFamily family, Vector2 direction)
        {
            switch (family)
            {
                case AxisFamily.X:
                    return direction.X >= 0 ? Vector2.UnitX : -Vector2.UnitX;
                case AxisFamily.Y:
                    return direction.Y >= 0 ? Vector2.UnitY : -Vector2.UnitY;
                default:
                    return direction.Normalized();
            }
        }
    }
}
