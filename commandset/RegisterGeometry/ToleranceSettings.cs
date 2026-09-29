using System;

namespace RegisterGeometry
{
    /// <summary>
    ///     Tolerance bounds applied to all derivation primitives. Mirrors the
    ///     DTO contract one-to-one so callers can construct it from a
    ///     deserialised payload without losing precision.
    /// </summary>
    public readonly struct ToleranceSettings
    {
        /// <summary>
        ///     Default tolerances: 1° angular, 1 mm intersection, 50 mm grouping,
        ///     300 mm support search, 1 mm snap.
        /// </summary>
        public static readonly ToleranceSettings Default = new ToleranceSettings(
            angularDegrees: 1.0,
            intersectionMm: 1.0,
            groupingMm: 50.0,
            supportSearchMm: 300.0,
            snapMm: 1.0);

        public ToleranceSettings(
            double angularDegrees,
            double intersectionMm,
            double groupingMm,
            double supportSearchMm,
            double snapMm)
        {
            AngularDegrees = angularDegrees;
            IntersectionMm = intersectionMm;
            GroupingMm = groupingMm;
            SupportSearchMm = supportSearchMm;
            SnapMm = snapMm;
        }

        public double AngularDegrees { get; }

        public double IntersectionMm { get; }

        public double GroupingMm { get; }

        public double SupportSearchMm { get; }

        public double SnapMm { get; }

        /// <summary>
        ///     Defensive copy with each value clamped to a positive lower
        ///     bound of 1e-6 (for length-style values) and 1e-3 for angles.
        /// </summary>
        public ToleranceSettings Sanitised()
        {
            return new ToleranceSettings(
                Math.Max(1e-3, AngularDegrees),
                Math.Max(1e-6, IntersectionMm),
                Math.Max(1e-6, GroupingMm),
                Math.Max(1e-6, SupportSearchMm),
                Math.Max(0.0, SnapMm));
        }

        /// <summary>
        ///     Tangent of the angular tolerance, precomputed for direction
        ///     classification.
        /// </summary>
        public double AngularToleranceRadians => AngularDegrees * Math.PI / 180.0;

        public override bool Equals(object? obj) =>
            obj is ToleranceSettings other &&
            AngularDegrees.Equals(other.AngularDegrees) &&
            IntersectionMm.Equals(other.IntersectionMm) &&
            GroupingMm.Equals(other.GroupingMm) &&
            SupportSearchMm.Equals(other.SupportSearchMm) &&
            SnapMm.Equals(other.SnapMm);

        public override int GetHashCode() => HashUtility.Combine(
            HashUtility.Of(AngularDegrees), HashUtility.Of(IntersectionMm),
            HashUtility.Of(GroupingMm), HashUtility.Of(SupportSearchMm), HashUtility.Of(SnapMm));
    }
}
