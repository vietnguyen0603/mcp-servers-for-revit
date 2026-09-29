using System;
using Autodesk.Revit.DB;
using RegisterGeometry;
using RevitMCPCommandSet.Services.DataExtraction.Register;
using GSegment = RegisterGeometry.Segment;

namespace RevitMCPCommandSet.Services.DataExtraction.ColumnWallExtraction
{
    /// <summary>
    ///     Converts a Revit <see cref="Wall"/> into the pure
    ///     <see cref="WallLeg"/> representation consumed by the geometry
    ///     layer. Stacked walls are flattened into a single leg because the
    ///     configured <see cref="ColumnWallRegisterExtractionOptions.MergeStackedWalls"/>
    ///     is reserved for a future phase; the current default behaviour
    ///     already collapses a stacked wall into one leg per Revit element.
    /// </summary>
    internal static class WallGeometryProbe
    {
        /// <summary>
        ///     Probe a wall and return its centreline + thickness. Throws
        ///     <see cref="InvalidOperationException"/> when the wall has no
        ///     location line (e.g. in-place mass walls or curtain walls
        ///     whose base is non-linear).
        /// </summary>
        public static WallLeg ExtractLeg(Wall wall, double snapMm)
        {
            if (wall == null) throw new ArgumentNullException(nameof(wall));
            var location = wall.Location as LocationCurve;
            if (location == null || location.Curve == null)
            {
                throw new InvalidOperationException("wall_has_no_location_curve");
            }

            var centreline = ConvertCurveToSegment(location.Curve, snapMm);
            double thicknessMm = ReadThicknessMm(wall);
            var mark = wall.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? string.Empty;

            return new WallLeg(
                uniqueId: wall.UniqueId,
                mark: mark,
                centreline: centreline,
                thicknessMm: thicknessMm,
                shape: WallShape.Unknown);
        }

        private static GSegment ConvertCurveToSegment(Curve curve, double snapMm)
        {
            switch (curve)
            {
                case Line line:
                {
                    var (sx, sy, _) = RegisterUnitConverter.ToPlanMm(line.GetEndPoint(0));
                    var (ex, ey, _) = RegisterUnitConverter.ToPlanMm(line.GetEndPoint(1));
                    return new GSegment(new Point2(sx, sy), new Point2(ex, ey));
                }
                case Arc arc:
                {
                    // For now we approximate an arc wall as a chord; the
                    // topology pass still detects single-leg plans as
                    // Planar and the builder emits the centreline so a
                    // future curved-wall pass can refine the shape.
                    var (sx, sy, _) = RegisterUnitConverter.ToPlanMm(arc.GetEndPoint(0));
                    var (ex, ey, _) = RegisterUnitConverter.ToPlanMm(arc.GetEndPoint(1));
                    return new GSegment(new Point2(sx, sy), new Point2(ex, ey));
                }
                default:
                {
                    // NURBS / hermite / etc: fall back to endpoints so the
                    // row still ships with a coarse centreline. The
                    // caller flags this with a warning.
                    var (sx, sy, _) = RegisterUnitConverter.ToPlanMm(curve.GetEndPoint(0));
                    var (ex, ey, _) = RegisterUnitConverter.ToPlanMm(curve.GetEndPoint(1));
                    return new GSegment(new Point2(sx, sy), new Point2(ex, ey));
                }
            }
        }

        private static double ReadThicknessMm(Wall wall)
        {
            try
            {
                var widthParam = wall.get_Parameter(BuiltInParameter.WALL_ATTR_WIDTH_PARAM);
                if (widthParam != null && widthParam.StorageType == StorageType.Double)
                {
                    return RegisterUnitConverter.MmFromFeet(widthParam.AsDouble());
                }
            }
            catch (InvalidOperationException)
            {
                // Some wall types don't expose a width parameter; fall
                // back to the type's compound structure.
            }

            try
            {
                var typeWidthParam = wall.WallType?.get_Parameter(BuiltInParameter.WALL_ATTR_WIDTH_PARAM);
                if (typeWidthParam != null && typeWidthParam.StorageType == StorageType.Double)
                {
                    return RegisterUnitConverter.MmFromFeet(typeWidthParam.AsDouble());
                }
            }
            catch (InvalidOperationException)
            {
                // Wall type lookup failed; the builder will treat the leg
                // as having zero thickness, which surfaces as a tiny
                // bounds in plan and triggers a warning downstream.
            }

            return 0.0;
        }
    }
}
