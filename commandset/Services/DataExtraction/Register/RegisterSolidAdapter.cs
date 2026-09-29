using System;
using Autodesk.Revit.DB;
using RegisterGeometry;

namespace RevitMCPCommandSet.Services.DataExtraction.Register
{
    /// <summary>
    ///     Adapts a Revit <see cref="Solid"/> into the planar geometry
    ///     primitives used by the register handlers. The adapter converts
    ///     the solid's bounding box into millimetres and exposes a helper
    ///     for sampling intersection points against another solid.
    /// </summary>
    /// <remarks>
    ///     The adapter never reads partial solid data; only the bounding
    ///     box and face/edge counts are used so it stays fast enough to run
    ///     for every beam support candidate.
    /// </remarks>
    public static class RegisterSolidAdapter
    {
        /// <summary>
        ///     Result of converting a Revit solid to planar bounds.
        /// </summary>
        public readonly struct Result
        {
            public Result(Bounds bounds, double minZmm, double maxZmm, int faceCount, int edgeCount)
            {
                Bounds = bounds;
                MinZmm = minZmm;
                MaxZmm = maxZmm;
                FaceCount = faceCount;
                EdgeCount = edgeCount;
            }

            public Bounds Bounds { get; }

            public double MinZmm { get; }
            public double MaxZmm { get; }

            public int FaceCount { get; }
            public int EdgeCount { get; }
        }

        /// <summary>
        ///     Convert a Revit solid to a planar bounds and a vertical extent
        ///     expressed in millimetres.
        /// </summary>
        public static Result ToPlanBounds(Solid solid)
        {
            if (solid == null || solid.Volume <= 0)
            {
                return new Result(Bounds.Empty, 0, 0, 0, 0);
            }
            var box = solid.GetBoundingBox();
            if (box == null)
            {
                return new Result(Bounds.Empty, 0, 0, solid.Faces.Size, solid.Edges.Size);
            }
            var (minX, minY, maxX, maxY) = RegisterUnitConverter.ToPlanMm(box);
            var bounds = new Bounds(minX, minY, maxX, maxY);
            var minZ = RegisterUnitConverter.MmFromFeet(box.Min.Z);
            var maxZ = RegisterUnitConverter.MmFromFeet(box.Max.Z);
            return new Result(bounds, Math.Min(minZ, maxZ), Math.Max(minZ, maxZ),
                solid.Faces.Size, solid.Edges.Size);
        }

        /// <summary>
        ///     Compute the closest planar distance (in millimetres) between
        ///     the bounds and a query point. Uses Chebyshev distance so the
        ///     result is invariant under rotation.
        /// </summary>
        public static double ChebyshevDistanceMm(Bounds bounds, double xMm, double yMm)
        {
            return bounds.ChebyshevDistanceTo(new Point2(xMm, yMm));
        }

        /// <summary>
        ///     Inflate bounds by <paramref name="paddingMm"/> on each side.
        ///     Useful for support search radii that exceed the literal bounds.
        /// </summary>
        public static Bounds Inflated(Bounds bounds, double paddingMm)
        {
            return bounds.Inflated(paddingMm);
        }

        /// <summary>
        ///     Try to compute the closest contact point (in millimetres)
        ///     between a beam axis and a support solid. The result is null
        ///     when the geometry cannot be intersected cheaply; callers
        ///     should fall back to bounds-distance heuristics.
        /// </summary>
        public static (double Xmm, double Ymm)? TryClosestPointMm(
            Solid supportSolid,
            XYZ beamAxisStart,
            XYZ beamAxisEnd)
        {
            if (supportSolid == null) throw new ArgumentNullException(nameof(supportSolid));
            if (beamAxisStart == null) throw new ArgumentNullException(nameof(beamAxisStart));
            if (beamAxisEnd == null) throw new ArgumentNullException(nameof(beamAxisEnd));

            // Sample the support bounds at the beam end sample. The
            // intersection result array is the cheapest path Revit offers
            // for curve-vs-solid.
            var beam = Line.CreateBound(beamAxisStart, beamAxisEnd);
            try
            {
                var options = new SolidCurveIntersectionOptions();
                var intersections = supportSolid.IntersectWithCurve(beam, options);
                if (intersections == null || intersections.SegmentCount == 0)
                {
                    return null;
                }
                var first = intersections.GetCurveSegment(0);
                if (first == null) return null;
                var hit = first.GetEndPoint(0);
                if (hit == null) return null;
                var (x, y, _) = RegisterUnitConverter.ToPlanMm(hit);
                return (x, y);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}