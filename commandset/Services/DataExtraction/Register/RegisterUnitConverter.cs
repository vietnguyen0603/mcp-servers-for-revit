using System;
using System.Globalization;
using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Services.DataExtraction.Register
{
    /// <summary>
    ///     Conversion helpers between Revit internal feet and the millimetre
    ///     unit used by every register response. Also exposes a small wrapper
    ///     for converting an <see cref="XYZ"/> to planar millimetres in a
    ///     chosen coordinate frame (project or shared).
    /// </summary>
    /// <remarks>
    ///     The conversion is the constant <c>304.8 mm = 1 internal foot</c>.
    ///     Because both unit representations are exact, no rounding logic is
    ///     applied beyond the IEEE-754 round-trip format used by the DTO
    ///     serialiser. All helpers are allocation-free.
    /// </remarks>
    public static class RegisterUnitConverter
    {
        /// <summary>
        ///     Millimetres per Revit internal foot.
        /// </summary>
        public const double MillimetresPerFoot = 304.8;

        /// <summary>
        ///     Convert millimetres to Revit internal feet.
        /// </summary>
        public static double FeetFromMm(double mm)
        {
            return mm / MillimetresPerFoot;
        }

        /// <summary>
        ///     Convert Revit internal feet to millimetres.
        /// </summary>
        public static double MmFromFeet(double feet)
        {
            return feet * MillimetresPerFoot;
        }

        /// <summary>
        ///     Convert a single XYZ coordinate (Revit internal feet) to planar
        ///     millimetres in plan view (Z is returned as a separate elevation
        ///     field in millimetres so callers can use it for sloped beams).
        /// </summary>
        public static (double Xmm, double Ymm, double Zmm) ToPlanMm(XYZ xyz)
        {
            if (xyz == null) throw new ArgumentNullException(nameof(xyz));
            return (
                MmFromFeet(xyz.X),
                MmFromFeet(xyz.Y),
                MmFromFeet(xyz.Z));
        }

        /// <summary>
        ///     Convert a Revit bounding box (XYZ, internal feet) into planar
        ///     millimetres. Z is exposed but typically ignored because
        ///     register responses are projected to plan.
        /// </summary>
        public static (double MinXmm, double MinYmm, double MaxXmm, double MaxYmm)
            ToPlanMm(BoundingBoxXYZ box)
        {
            if (box == null) throw new ArgumentNullException(nameof(box));
            return (
                MmFromFeet(box.Min.X),
                MmFromFeet(box.Min.Y),
                MmFromFeet(box.Max.X),
                MmFromFeet(box.Max.Y));
        }

        /// <summary>
        ///     Format a length in millimetres using the round-trip format and
        ///     invariant culture. Centralised so the DTO serialiser and any
        ///     ad-hoc debug log agree on the textual representation.
        /// </summary>
        public static string FormatMm(double mm)
        {
            return mm.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>
        ///     Project an <see cref="XYZ"/> onto the XY plane and apply an
        ///     optional transformation. The supplied <paramref name="planar"/>
        ///     flag controls whether Z is preserved (false for planar records).
        ///     The transform is treated as a homogeneous 4x4 matrix in
        ///     millimetres; this keeps the conversion in double precision
        ///     without round-tripping through the Revit Transform type.
        /// </summary>
        public static XYZ ApplyTransform(XYZ xyz, Transform transform, bool planar)
        {
            if (xyz == null) throw new ArgumentNullException(nameof(xyz));
            if (transform == null) return planar ? new XYZ(xyz.X, xyz.Y, 0) : xyz;
            var t = transform;
            double x = t.BasisX.X * xyz.X + t.BasisX.Y * xyz.Y + t.BasisX.Z * xyz.Z + t.Origin.X;
            double y = t.BasisY.X * xyz.X + t.BasisY.Y * xyz.Y + t.BasisY.Z * xyz.Z + t.Origin.Y;
            double z = planar
                ? 0
                : t.BasisZ.X * xyz.X + t.BasisZ.Y * xyz.Y + t.BasisZ.Z * xyz.Z + t.Origin.Z;
            return new XYZ(x, y, z);
        }
    }
}