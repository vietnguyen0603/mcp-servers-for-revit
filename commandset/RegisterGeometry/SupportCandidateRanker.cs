using System;
using System.Collections.Generic;
using System.Linq;

namespace RegisterGeometry
{
    /// <summary>
    ///     Ranks <see cref="SupportCandidate"/>s near a beam end and picks the
    ///     best one. The ranking is deterministic and combines, in priority
    ///     order:
    ///     <list type="number">
    ///         <item>distance to the beam-end sample point,</item>
    ///         <item>category precedence configured by the caller,</item>
    ///         <item>stable tie-breaking on the candidate's unique id.</item>
    ///     </list>
    ///     All scoring is pure and reproducible.
    /// </summary>
    public sealed class SupportCandidateRanker
    {
        private readonly ToleranceSettings _tolerance;
        private readonly IReadOnlyDictionary<SupportCategory, int> _precedence;
        private readonly double _maxDistance;

        public SupportCandidateRanker(
            ToleranceSettings tolerance,
            IReadOnlyDictionary<SupportCategory, int>? precedence = null)
        {
            _tolerance = tolerance;
            _precedence = precedence ?? DefaultPrecedence();
            _maxDistance = Math.Max(_tolerance.SupportSearchMm, 1e-6);
        }

        /// <summary>
        ///     Default precedence: Wall &lt; Column &lt; Beam &lt; Other. Smaller
        ///     rank wins when distances tie.
        /// </summary>
        public static IReadOnlyDictionary<SupportCategory, int> DefaultPrecedence()
        {
            return new Dictionary<SupportCategory, int>
            {
                [SupportCategory.Wall] = 0,
                [SupportCategory.Column] = 1,
                [SupportCategory.Beam] = 2,
                [SupportCategory.Other] = 3,
                [SupportCategory.Unknown] = 4,
            };
        }

        /// <summary>
        ///     Filter <paramref name="candidates"/> to those within
        ///     <see cref="ToleranceSettings.SupportSearchMm"/> of the beam-end
        ///     sample point and rank them.
        /// </summary>
        public IReadOnlyList<SupportCandidate> Prune(IEnumerable<SupportCandidate> candidates, Point2 beamEndSample)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            var result = new List<SupportCandidate>();
            foreach (var candidate in candidates)
            {
                if (candidate == null) continue;
                if (candidate.DistanceMm > _maxDistance) continue;
                // Reject candidates whose bounds cannot enclose the beam-end sample
                // (slight margin to allow for face contact at the bound).
                if (candidate.Bounds.ChebyshevDistanceTo(beamEndSample) > _maxDistance) continue;
                result.Add(candidate);
            }
            return result;
        }

        /// <summary>
        ///     Select the best candidate from <paramref name="candidates"/>.
        ///     The candidate must be within the support search tolerance of
        ///     <paramref name="beamEndSample"/>; otherwise null is returned.
        /// </summary>
        public SupportSelection Select(IEnumerable<SupportCandidate> candidates, Point2 beamEndSample)
        {
            var pruned = Prune(candidates, beamEndSample);
            if (pruned.Count == 0)
            {
                return new SupportSelection(null, Array.Empty<SupportCandidate>());
            }

            var ranked = pruned
                .OrderBy(c => c.DistanceMm)
                .ThenBy(c => CategoryRank(c.Category))
                .ThenBy(c => c.UniqueId, StringComparer.Ordinal)
                .ToList();
            return new SupportSelection(ranked[0], ranked);
        }

        /// <summary>
        ///     Lower precedence rank wins. Returns 999 for unknown categories
        ///     so they always lose to configured ones.
        /// </summary>
        public int CategoryRank(SupportCategory category)
        {
            return _precedence.TryGetValue(category, out var rank) ? rank : 999;
        }

        /// <summary>
        ///     Compute a contact station (millimetres from beam start) when the
        ///     selected candidate is a wall or column. Returns null when the
        ///     contact cannot be determined from the supplied geometry.
        /// </summary>
        public static double? ComputeContactStation(
            Point2 beamStart,
            Point2 beamEnd,
            Point2 beamEndSample,
            Point2 contactPoint)
        {
            var axis = new Vector2(beamEnd.X - beamStart.X, beamEnd.Y - beamStart.Y);
            double length = axis.Length;
            if (length <= 1e-9) return null;
            axis = axis.Normalized();
            var rel = new Vector2(contactPoint.X - beamStart.X, contactPoint.Y - beamStart.Y);
            double station = rel.Dot(axis);
            // Sanity-clamp to beam length; negative or beyond the end indicates
            // a near-miss and the caller should treat it as a non-contact.
            if (station < -1e-3 || station > length + 1e-3) return null;
            // Snap to the beam-end sample if within a tiny fraction of the
            // length, so candidates touching the end sample report 0/L.
            const double tolerance = 1e-3;
            if (Math.Abs(station) < tolerance) return 0;
            if (Math.Abs(station - length) < tolerance) return length;
            return station;
        }
    }
}
