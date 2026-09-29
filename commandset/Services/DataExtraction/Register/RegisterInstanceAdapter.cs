using System;
using Autodesk.Revit.DB;
using RegisterGeometry;

namespace RevitMCPCommandSet.Services.DataExtraction.Register
{
    /// <summary>
    ///     Adapts a Revit <see cref="FamilyInstance"/> (or other instance
    ///     element) to the planar geometry primitives used by the register
    ///     handlers. The adapter exposes the instance transform, location
    ///     curve (when present), and the planar bounding box of the
    ///     instance's geometry in millimetres.
    /// </summary>
    /// <remarks>
    ///     The adapter is built around <see cref="FamilyInstance"/> but also
    ///     accepts any <see cref="Element"/> whose geometry can be queried.
    ///     Static helpers expose location-curve sampling and bounding-box
    ///     conversion so the column/wall and beam handlers can share the
    ///     same code paths.
    /// </remarks>
    public static class RegisterInstanceAdapter
    {
        /// <summary>
        ///     Result of an instance location/transform probe.
        /// </summary>
        public readonly struct ProbeResult
        {
            public ProbeResult(
                Transform instanceTransform,
                ICurve2 locationCurve,
                Bounds planarBoundsMm,
                double minZmm,
                double maxZmm)
            {
                InstanceTransform = instanceTransform;
                LocationCurve = locationCurve;
                PlanarBoundsMm = planarBoundsMm;
                MinZmm = minZmm;
                MaxZmm = maxZmm;
            }

            public Transform InstanceTransform { get; }
            public ICurve2 LocationCurve { get; }
            public Bounds PlanarBoundsMm { get; }
            public double MinZmm { get; }
            public double MaxZmm { get; }
        }

        /// <summary>
        ///     Probe the instance for its transform, location curve, and
        ///     planar bounds. The location curve is null when the instance
        ///     has no <see cref="LocationCurve"/> (e.g. a point-based family).
        /// </summary>
        public static ProbeResult Probe(Element element)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            var transform = TryGetTransform(element);
            var curve = TryGetLocationCurve(element);
            var bounds = TryGetPlanarBounds(element);
            return new ProbeResult(transform, curve, bounds.Bounds, bounds.MinZmm, bounds.MaxZmm);
        }

        /// <summary>
        ///     Return the instance transform when <paramref name="element"/>
        ///     is a <see cref="FamilyInstance"/>. Null for everything else.
        /// </summary>
        public static Transform TryGetTransform(Element element)
        {
            if (element is FamilyInstance fi)
            {
                try
                {
                    return fi.GetTransform();
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }
            return null;
        }

        /// <summary>
        ///     Convert the instance's <see cref="LocationCurve"/> into a
        ///     <see cref="ICurve2"/> in millimetres. Null when the element
        ///     has no location curve or the curve is not representable.
        /// </summary>
        public static ICurve2 TryGetLocationCurve(Element element)
        {
            if (element?.Location is not LocationCurve locationCurve) return null;
            if (locationCurve.Curve == null) return null;
            var result = RegisterCurveAdapter.ToCurve2(locationCurve.Curve);
            return result.IsConverted ? result.Curve : null;
        }

        /// <summary>
        ///     Project the element's geometry bounding box to plan. The
        ///     result is in millimetres with an empty bounds box when the
        ///     element has no geometry (e.g. some nested instances).
        /// </summary>
        public static (Bounds Bounds, double MinZmm, double MaxZmm) TryGetPlanarBounds(Element element)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            try
            {
                var bbox = element.get_BoundingBox(null);
                if (bbox == null) return (Bounds.Empty, 0, 0);
                var (minX, minY, maxX, maxY) = RegisterUnitConverter.ToPlanMm(bbox);
                var minZ = RegisterUnitConverter.MmFromFeet(bbox.Min.Z);
                var maxZ = RegisterUnitConverter.MmFromFeet(bbox.Max.Z);
                return (new Bounds(minX, minY, maxX, maxY), minZ, maxZ);
            }
            catch (InvalidOperationException)
            {
                return (Bounds.Empty, 0, 0);
            }
        }

        /// <summary>
        ///     Plan sample point for a location-based element. Returns null
        ///     when the element has no location (e.g. point-based families
        ///     that still want a centroid).
        /// </summary>
        public static Point2? TryGetSamplePoint(Element element)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            var curve = TryGetLocationCurve(element);
            if (curve != null) return curve.SamplePoint;

            // Fall back to bounding box centre so the caller always has a
            // deterministic planar reference.
            var (bounds, _, _) = TryGetPlanarBounds(element);
            if (bounds.IsEmpty) return null;
            return bounds.Centre;
        }

        /// <summary>
        ///     Planar footprint dimensions of an instance, derived from the
        ///     bounding box. Width is along X and depth along Y in the
        ///     host's project frame.
        /// </summary>
        public static (double WidthMm, double DepthMm, double CenterXmm, double CenterYmm)
            TryGetPlanarFootprintMm(Element element)
        {
            var (bounds, _, _) = TryGetPlanarBounds(element);
            if (bounds.IsEmpty)
            {
                return (0, 0, 0, 0);
            }
            return (bounds.Width, bounds.Height, bounds.Centre.X, bounds.Centre.Y);
        }
    }
}
