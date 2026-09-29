using System;

namespace RegisterGeometry
{
    /// <summary>
    ///     Planar circular arc defined by start/end points plus a centre. The
    ///     arc is parameterised counter-clockwise from <see cref="Start"/> to
    ///     <see cref="End"/>.
    /// </summary>
    public readonly struct ArcCurve2 : ICurve2, IEquatable<ArcCurve2>
    {
        public ArcCurve2(Point2 start, Point2 end, Point2 centre, double radius, bool isFullCircle)
        {
            Start = start;
            End = end;
            Centre = centre;
            Radius = radius;
            IsFullCircle = isFullCircle;
        }

        public Point2 Start { get; }

        public Point2 End { get; }

        public Point2 Centre { get; }

        public double Radius { get; }

        public bool IsFullCircle { get; }

        public CurveKind Kind => CurveKind.Arc;

        public Point2 SamplePoint
        {
            get
            {
                if (IsFullCircle) return Centre;
                // midpoint by arc angle
                double startAngle = Math.Atan2(Start.Y - Centre.Y, Start.X - Centre.X);
                double endAngle = Math.Atan2(End.Y - Centre.Y, End.X - Centre.X);
                double mid = startAngle + Sweep(startAngle, endAngle) * 0.5;
                return new Point2(Centre.X + Radius * Math.Cos(mid), Centre.Y + Radius * Math.Sin(mid));
            }
        }

        public Vector2 Tangent
        {
            get
            {
                if (IsFullCircle)
                {
                    // pick tangent at angle 0 for determinism
                    return new Vector2(0, 1);
                }
                double startAngle = Math.Atan2(Start.Y - Centre.Y, Start.X - Centre.X);
                double endAngle = Math.Atan2(End.Y - Centre.Y, End.X - Centre.X);
                double mid = startAngle + Sweep(startAngle, endAngle) * 0.5;
                // tangent perpendicular to radius, counter-clockwise
                return new Vector2(-Math.Sin(mid), Math.Cos(mid));
            }
        }

        public Bounds Bounds
        {
            get
            {
                if (IsFullCircle)
                {
                    return new Bounds(
                        Centre.X - Radius, Centre.Y - Radius,
                        Centre.X + Radius, Centre.Y + Radius);
                }
                // conservative: union of endpoints and centre-extrema (axis-aligned circle box)
                double startAngle = Math.Atan2(Start.Y - Centre.Y, Start.X - Centre.X);
                double endAngle = Math.Atan2(End.Y - Centre.Y, End.X - Centre.X);
                double sweep = Sweep(startAngle, endAngle);
                double minX = Math.Min(Start.X, End.X);
                double minY = Math.Min(Start.Y, End.Y);
                double maxX = Math.Max(Start.X, End.X);
                double maxY = Math.Max(Start.Y, End.Y);

                // Include any axis-aligned extremum hit during the sweep.
                for (int i = 0; i < 4; i++)
                {
                    double target = i * Math.PI / 2;
                    if (IsAngleWithinSweep(target, startAngle, sweep))
                    {
                        double ex = Centre.X + Radius * Math.Cos(target);
                        double ey = Centre.Y + Radius * Math.Sin(target);
                        if (ex < minX) minX = ex;
                        if (ey < minY) minY = ey;
                        if (ex > maxX) maxX = ex;
                        if (ey > maxY) maxY = ey;
                    }
                }
                return new Bounds(minX, minY, maxX, maxY);
            }
        }

        public double Length
        {
            get
            {
                if (IsFullCircle) return 2 * Math.PI * Radius;
                double startAngle = Math.Atan2(Start.Y - Centre.Y, Start.X - Centre.X);
                double endAngle = Math.Atan2(End.Y - Centre.Y, End.X - Centre.X);
                return Math.Abs(Sweep(startAngle, endAngle)) * Radius;
            }
        }

        public double StartAngleDeg => Math.Atan2(Start.Y - Centre.Y, Start.X - Centre.X) * 180.0 / Math.PI;

        public double EndAngleDeg => Math.Atan2(End.Y - Centre.Y, End.X - Centre.X) * 180.0 / Math.PI;

        public double SweepAngleDeg
        {
            get
            {
                if (IsFullCircle) return 360.0;
                double startAngle = Math.Atan2(Start.Y - Centre.Y, Start.X - Centre.X);
                double endAngle = Math.Atan2(End.Y - Centre.Y, End.X - Centre.X);
                return Sweep(startAngle, endAngle) * 180.0 / Math.PI;
            }
        }

        public bool Equals(ArcCurve2 other) =>
            Start.Equals(other.Start) && End.Equals(other.End) &&
            Centre.Equals(other.Centre) && Radius.Equals(other.Radius) &&
            IsFullCircle == other.IsFullCircle;

        public override bool Equals(object? obj) => obj is ArcCurve2 other && Equals(other);

        public override int GetHashCode() => HashUtility.Combine(
            HashUtility.Of(Start), HashUtility.Of(End),
            HashUtility.Of(Centre), HashUtility.Of(Radius),
            HashUtility.Of(IsFullCircle));

        private static double Normalise(double angle)
        {
            double twoPi = 2 * Math.PI;
            double a = angle % twoPi;
            if (a < 0) a += twoPi;
            return a;
        }

        private static double Sweep(double from, double to)
        {
            double a = Normalise(from);
            double b = Normalise(to);
            double delta = b - a;
            if (delta < 0) delta += 2 * Math.PI;
            return delta;
        }

        private static bool IsAngleWithinSweep(double target, double startAngle, double sweep)
        {
            double a = Normalise(startAngle);
            double t = Normalise(target);
            double end = a + sweep;
            if (end <= 2 * Math.PI)
            {
                return t >= a && t <= end;
            }
            return t >= a || t <= end - 2 * Math.PI;
        }
    }
}
