using System;
using Autodesk.Revit.DB;
using RegisterGeometry;
using RSegment = RegisterGeometry.Segment;

namespace RevitMCPCommandSet.Services.DataExtraction.Register
{
    /// <summary>
    ///     Adapts a Revit <see cref="Curve"/> to the pure <see cref="ICurve2"/>
    ///     geometry primitives used by the register handlers. Coordinates are
    ///     converted to millimetres by <see cref="RegisterUnitConverter"/> so
    ///     the geometry layer never sees internal feet.
    /// </summary>
    public static class RegisterCurveAdapter
    {
        /// <summary>
        ///     Result of attempting to convert a Revit curve.
        /// </summary>
        public readonly struct Result
        {
            public Result(ICurve2 curve, bool isFullCircle, bool unsupported, string reason)
            {
                Curve = curve;
                IsFullCircle = isFullCircle;
                Unsupported = unsupported;
                Reason = reason;
            }

            public ICurve2 Curve { get; }

            /// <summary>
            ///     True when the source curve is a full-circle arc.
            /// </summary>
            public bool IsFullCircle { get; }

            /// <summary>
            ///     True when the adapter could not convert the source curve.
            ///     The handler should record a warning and skip the record.
            /// </summary>
            public bool Unsupported { get; }

            public string Reason { get; }

            public bool IsConverted => Curve != null && !Unsupported;
        }

        /// <summary>
        ///     Convert a Revit curve to an <see cref="ICurve2"/>. Lines and
        ///     arcs are supported; everything else is flagged unsupported.
        /// </summary>
        public static Result ToCurve2(Curve revitCurve)
        {
            if (revitCurve == null)
            {
                return new Result(null, false, true, "null_curve");
            }

            switch (revitCurve)
            {
                case Line line:
                    return FromLine(line);
                case Arc arc:
                    return FromArc(arc);
                case Ellipse ellipse when Math.Abs(ellipse.RadiusX - ellipse.RadiusY) <= 1e-6:
                    return FromCircularEllipse(ellipse);
                default:
                    return new Result(null, false, true,
                        $"unsupported_curve_kind:{revitCurve.GetType().Name}");
            }
        }

        private static Result FromLine(Line line)
        {
            var (sx, sy, _) = RegisterUnitConverter.ToPlanMm(line.GetEndPoint(0));
            var (ex, ey, _) = RegisterUnitConverter.ToPlanMm(line.GetEndPoint(1));
            var seg = new RSegment(new Point2(sx, sy), new Point2(ex, ey));
            return new Result(seg, false, false, null);
        }

        private static Result FromArc(Arc arc)
        {
            var (cx, cy, _) = RegisterUnitConverter.ToPlanMm(arc.Center);
            var (sx, sy, _) = RegisterUnitConverter.ToPlanMm(arc.GetEndPoint(0));
            var (ex, ey, _) = RegisterUnitConverter.ToPlanMm(arc.GetEndPoint(1));
            bool fullCircle = arc.IsClosed
                              && sx == ex && sy == ey;
            var radiusMm = RegisterUnitConverter.MmFromFeet(arc.Radius);
            var curve2 = new ArcCurve2(
                new Point2(sx, sy),
                new Point2(ex, ey),
                new Point2(cx, cy),
                radiusMm,
                fullCircle);
            return new Result(curve2, fullCircle, false, null);
        }

        private static Result FromCircularEllipse(Ellipse ellipse)
        {
            // Approximate as a full-circle arc. Plan projection is required
            // because ellipse axes live in 3D.
            var centre = ellipse.Center;
            var xRadius = RegisterUnitConverter.MmFromFeet(ellipse.RadiusX);
            var yRadius = RegisterUnitConverter.MmFromFeet(ellipse.RadiusY);
            if (Math.Abs(xRadius - yRadius) > 1e-6)
            {
                return new Result(null, false, true, "ellipse_axes_mismatch");
            }
            var (cx, cy, _) = RegisterUnitConverter.ToPlanMm(centre);
            var sample = ellipse.GetEndPoint(0);
            var (sx, sy, _) = RegisterUnitConverter.ToPlanMm(sample);
            var curve = new ArcCurve2(
                new Point2(sx, sy),
                new Point2(sx, sy),
                new Point2(cx, cy),
                xRadius,
                isFullCircle: true);
            return new Result(curve, true, false, null);
        }

        /// <summary>
        ///     Project the curve to plan and convert to millimetres in one
        ///     step. The resulting curve is suitable for serialisation as
        ///     <see cref="BeamLocationCurveDto"/> or as a grid's plan geometry.
        /// </summary>
        public static (PlanarPoint start, PlanarPoint end, double lengthMm, double zStartMm, double zEndMm)
            ToPlanMeasurementsMm(Curve revitCurve)
        {
            if (revitCurve == null) throw new ArgumentNullException(nameof(revitCurve));
            var (sx, sy, sz0) = RegisterUnitConverter.ToPlanMm(revitCurve.GetEndPoint(0));
            var (ex, ey, ez1) = RegisterUnitConverter.ToPlanMm(revitCurve.GetEndPoint(1));
            double length = RegisterUnitConverter.MmFromFeet(revitCurve.Length);
            return (
                new PlanarPoint(sx, sy),
                new PlanarPoint(ex, ey),
                length,
                sz0,
                ez1);
        }
    }

    /// <summary>
    ///     Planar point in millimetres. Produced by
    ///     <see cref="RegisterCurveAdapter.ToPlanMeasurementsMm"/>; consumers
    ///     can map it onto the DTO's <see cref="Models.DataExtraction.Register.PlanPoint2D"/>.
    /// </summary>
    public readonly struct PlanarPoint
    {
        public PlanarPoint(double xMm, double yMm)
        {
            Xmm = xMm;
            Ymm = yMm;
        }

        public double Xmm { get; }
        public double Ymm { get; }

        public Models.DataExtraction.Register.PlanPoint2D ToDto() => new()
        {
            X = Xmm,
            Y = Ymm,
        };
    }
}