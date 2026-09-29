using System;
using System.Collections.Generic;

namespace RegisterGeometry
{
    /// <summary>
    ///     Mirrors <c>Models.DataExtraction.Register.SupportCategory</c> for
    ///     the geometry layer. Handlers map at the boundary.
    /// </summary>
    public enum SupportCategory
    {
        Unknown = 0,
        Wall = 1,
        Column = 2,
        Beam = 3,
        Other = 4,
    }

    /// <summary>
    ///     A pure support candidate near one end of a beam. The candidate
    ///     exposes enough metadata for the ranker to score it without taking
    ///     a dependency on Revit types.
    /// </summary>
    public sealed class SupportCandidate
    {
        public SupportCandidate(
            string uniqueId,
            SupportCategory category,
            Bounds bounds,
            Point2 contactPoint,
            double distanceMm)
        {
            UniqueId = uniqueId ?? throw new ArgumentNullException(nameof(uniqueId));
            Category = category;
            Bounds = bounds;
            ContactPoint = contactPoint;
            DistanceMm = distanceMm;
        }

        public string UniqueId { get; }

        public SupportCategory Category { get; }

        public Bounds Bounds { get; }

        public Point2 ContactPoint { get; }

        /// <summary>
        ///     Distance from the beam-end sample to the candidate's contact
        ///     point, in millimetres. Smaller is better.
        /// </summary>
        public double DistanceMm { get; }

        /// <summary>
        ///     Optional element id for logging / display. The geometry layer
        ///     treats this as opaque metadata; ranking never depends on it.
        /// </summary>
        public long? ElementId { get; set; }

        /// <summary>
        ///     Optional element mark.
        /// </summary>
        public string? Mark { get; set; }

        public override string ToString() =>
            $"SupportCandidate({Category}, uid={UniqueId}, dist={DistanceMm:F2})";
    }

    /// <summary>
    ///     Result of a single support lookup. <see cref="Selected"/> is null
    ///     when no candidate satisfied the configuration; in that case
    ///     <see cref="IsCantilever"/> should be set by the caller.
    /// </summary>
    public sealed class SupportSelection
    {
        public SupportSelection(SupportCandidate? selected, IReadOnlyList<SupportCandidate> considered)
        {
            Selected = selected;
            Considered = considered ?? Array.Empty<SupportCandidate>();
        }

        public SupportCandidate? Selected { get; }

        public IReadOnlyList<SupportCandidate> Considered { get; }

        public double Score => Selected != null ? ComputeScore(Selected) : double.PositiveInfinity;

        private static double ComputeScore(SupportCandidate c) => c.DistanceMm;
    }
}
