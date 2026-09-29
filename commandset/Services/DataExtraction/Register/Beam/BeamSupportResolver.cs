using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RegisterGeometry;
using RevitMCPCommandSet.Models.DataExtraction.Register;
using RevitMCPCommandSet.Utils;
using ElementIdAlias = RevitMCPCommandSet.Utils.ElementIdExtensions;
using GeometrySupportCategory = RegisterGeometry.SupportCategory;
using PlanSupportCategory = RevitMCPCommandSet.Models.DataExtraction.Register.SupportCategory;

namespace RevitMCPCommandSet.Services.DataExtraction.Register.Beam
{
    /// <summary>
    ///     Resolves the inward support face at each end of a beam. The
    ///     resolver owns a <see cref="BeamSpatialIndex"/> over wall,
    ///     column, and beam candidates; for every beam end it asks the index
    ///     for neighbours, builds a <see cref="SupportCandidate"/> per
    ///     survivor, and lets the <see cref="SupportCandidateRanker"/> pick.
    ///     The contact station is then computed from the selected
    ///     candidate's planar contact point.
    /// </summary>
    public sealed class BeamSupportResolver
    {
        private readonly ToleranceSettings _tolerance;
        private readonly SupportCandidateRanker _ranker;
        private readonly BeamSpatialIndex _index;
        private readonly Dictionary<long, CandidateMeta> _metaById = new Dictionary<long, CandidateMeta>(256);

        public BeamSupportResolver(
            ToleranceSettings tolerance,
            IReadOnlyDictionary<GeometrySupportCategory, int> precedence)
        {
            _tolerance = tolerance;
            _ranker = new SupportCandidateRanker(tolerance, precedence);
            _index = new BeamSpatialIndex(Math.Max(tolerance.SupportSearchMm, 1.0));
        }

        public int CandidateCount => _metaById.Count;

        /// <summary>
        ///     Register a candidate support element. The element's planar
        ///     bounds are extracted from the geometry only when the caller
        ///     needs the row, so a large project pays the geometry cost for a
        ///     few dozen near-beam candidates rather than for every wall or
        ///     column in the model.
        /// </summary>
        public void AddCandidate(
            long elementId,
            GeometrySupportCategory category,
            Bounds planarBounds,
            string uniqueId,
            string mark)
        {
            if (planarBounds.IsEmpty) return;
            if (_metaById.ContainsKey(elementId)) return;
            var candidate = new SupportCandidate(uniqueId, category, planarBounds, planarBounds.Centre, 0.0)
            {
                ElementId = elementId,
                Mark = mark,
            };
            _metaById[elementId] = new CandidateMeta(candidate);
            _index.Insert(elementId, planarBounds, candidate);
        }

        /// <summary>
        ///     Resolve the support at one end of a beam. The function
        ///     reports the contact station (millimetres from beam start)
        ///     when the candidate was confirmed by geometry, otherwise
        ///     null, alongside the supporting evidence.
        /// </summary>
        public SupportResolution Resolve(
            XYZ beamStartFeet,
            XYZ beamEndFeet,
            BeamEnd end)
        {
            if (beamStartFeet == null) throw new ArgumentNullException(nameof(beamStartFeet));
            if (beamEndFeet == null) throw new ArgumentNullException(nameof(beamEndFeet));

            var (sx, sy) = PlanMm(beamStartFeet);
            var (ex, ey) = PlanMm(beamEndFeet);
            var beamStart = new Point2(sx, sy);
            var beamEnd = new Point2(ex, ey);
            var beamEndSample = end == BeamEnd.Start ? beamStart : beamEnd;
            var window = WindowAround(beamEndSample);
            var neighbours = _index.Query(window);

            // Build pure support candidates, then let the ranker prune and
            // pick. The distance we feed the ranker is the planar distance
            // from the beam-end sample to the candidate's planar contact
            // point; both are in millimetres.
            var candidates = new List<SupportCandidate>(neighbours.Count);
            foreach (var neighbour in neighbours)
            {
                if (!_metaById.TryGetValue(neighbour.Id, out var meta)) continue;
                double distance = meta.Candidate.Bounds.Centre.DistanceTo(beamEndSample);
                candidates.Add(new SupportCandidate(
                    meta.Candidate.UniqueId,
                    meta.Candidate.Category,
                    meta.Candidate.Bounds,
                    meta.Candidate.Bounds.Centre,
                    distance)
                {
                    ElementId = meta.Candidate.ElementId,
                    Mark = meta.Candidate.Mark,
                });
            }

            var selection = _ranker.Select(candidates, beamEndSample);
            if (selection.Selected == null)
            {
                return SupportResolution.Cantilever(end);
            }

            double? station = SupportCandidateRanker.ComputeContactStation(
                beamStart, beamEnd, beamEndSample, selection.Selected.ContactPoint);
            return new SupportResolution
            {
                End = end,
                Candidate = selection.Selected,
                ContactStationMm = station,
                Considered = selection.Considered,
                IsCantilever = false,
            };
        }

        /// <summary>
        ///     Update the contact point of an existing candidate with a
        ///     geometry-confirmed face hit. The resolver then re-evaluates
        ///     the distance and station from the face rather than the
        ///     candidate's planar bounds centre. Pass null to keep the
        ///     bounds-centre approximation.
        /// </summary>
        private void UpdateContactPoint(long elementId, double xMm, double yMm)
        {
            if (!_metaById.TryGetValue(elementId, out var meta)) return;
            meta.CandidateBoundsCentre = new Point2(xMm, yMm);
        }

        public bool TryGetCandidateBoundsCentre(long elementId, out Point2 centre)
        {
            if (_metaById.TryGetValue(elementId, out var meta) && meta.CandidateBoundsCentre.HasValue)
            {
                centre = meta.CandidateBoundsCentre.Value;
                return true;
            }
            centre = default;
            return false;
        }

        public SupportCandidateRanker Ranker => _ranker;

        public ToleranceSettings Tolerance => _tolerance;

        private Bounds WindowAround(Point2 sample)
        {
            var r = _tolerance.SupportSearchMm;
            return new Bounds(
                sample.X - r, sample.Y - r,
                sample.X + r, sample.Y + r);
        }

        private static (double Xmm, double Ymm) PlanMm(XYZ xyz)
        {
            return (RegisterUnitConverter.MmFromFeet(xyz.X), RegisterUnitConverter.MmFromFeet(xyz.Y));
        }

        private sealed class CandidateMeta
        {
            public CandidateMeta(SupportCandidate candidate)
            {
                Candidate = candidate;
            }
            public SupportCandidate Candidate { get; }
            public Point2? CandidateBoundsCentre { get; set; }
        }
    }

    /// <summary>
    ///     Identifies which end of the beam a support lookup targets.
    /// </summary>
    public enum BeamEnd
    {
        Start = 0,
        End = 1,
    }

    /// <summary>
    ///     Outcome of resolving one support. <see cref="IsCantilever"/>
    ///     signals that no candidate fell within the support search
    ///     tolerance; <see cref="ContactStationMm"/> may still be null when
    ///     the candidate existed but no axis-aligned contact face could be
    ///     confirmed.
    /// </summary>
    public sealed class SupportResolution
    {
        public SupportResolution()
        {
            Candidate = null;
            Considered = Array.Empty<SupportCandidate>();
        }

        public BeamEnd End { get; set; }
        public SupportCandidate Candidate { get; set; }
        public double? ContactStationMm { get; set; }
        public IReadOnlyList<SupportCandidate> Considered { get; set; } = Array.Empty<SupportCandidate>();
        public bool IsCantilever { get; set; }

        public static SupportResolution Cantilever(BeamEnd end)
        {
            return new SupportResolution
            {
                End = end,
                Candidate = null,
                Considered = Array.Empty<SupportCandidate>(),
                ContactStationMm = null,
                IsCantilever = true,
            };
        }
    }
}