using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction.Register
{
    /// <summary>
    ///     One record per physical Revit beam instance. Curved, sloped, joined
    ///     and cantilevered beams remain in output; axis/from/to grid fields
    ///     may be null for radial or non-tabular layouts.
    /// </summary>
    public class BeamRecord
    {
        /// <summary>
        ///     Stable provenance block.
        /// </summary>
        [JsonProperty("provenance")]
        public ElementProvenance Provenance { get; set; } = new ElementProvenance();

        /// <summary>
        ///     Element mark, resolved from configured parameter aliases.
        /// </summary>
        [JsonProperty("mark", NullValueHandling = NullValueHandling.Ignore)]
        public string Mark { get; set; }

        /// <summary>
        ///     Optional project classification (HB/VB/SP/B).
        /// </summary>
        [JsonProperty("classification", NullValueHandling = NullValueHandling.Ignore)]
        public string Classification { get; set; }

        /// <summary>
        ///     Section width in millimetres.
        /// </summary>
        [JsonProperty("widthMm")]
        public double WidthMm { get; set; }

        /// <summary>
        ///     Section depth in millimetres.
        /// </summary>
        [JsonProperty("depthMm")]
        public double DepthMm { get; set; }

        /// <summary>
        ///     Source parameter for <see cref="WidthMm"/> (built-in alias or
        ///     family parameter).
        /// </summary>
        [JsonProperty("widthSourceParameter", NullValueHandling = NullValueHandling.Ignore)]
        public string WidthSourceParameter { get; set; }

        /// <summary>
        ///     Source parameter for <see cref="DepthMm"/>.
        /// </summary>
        [JsonProperty("depthSourceParameter", NullValueHandling = NullValueHandling.Ignore)]
        public string DepthSourceParameter { get; set; }

        /// <summary>
        ///     Reference level name.
        /// </summary>
        [JsonProperty("levelName", NullValueHandling = NullValueHandling.Ignore)]
        public string LevelName { get; set; }

        /// <summary>
        ///     Vertical offset from the reference level (millimetres). Positive
        ///     is up.
        /// </summary>
        [JsonProperty("levelOffsetMm")]
        public double LevelOffsetMm { get; set; }

        /// <summary>
        ///     Physical location curve of the beam in 3D (millimetres).
        /// </summary>
        [JsonProperty("locationCurve")]
        public BeamLocationCurveDto LocationCurve { get; set; } = new BeamLocationCurveDto();

        /// <summary>
        ///     Plan projection start point in millimetres.
        /// </summary>
        [JsonProperty("startPointMm")]
        public PlanPoint2D StartPointMm { get; set; } = new PlanPoint2D();

        /// <summary>
        ///     Plan projection end point in millimetres.
        /// </summary>
        [JsonProperty("endPointMm")]
        public PlanPoint2D EndPointMm { get; set; } = new PlanPoint2D();

        /// <summary>
        ///     Axis grid reference at the start (null when the layout is radial
        ///     or otherwise unresolved).
        /// </summary>
        [JsonProperty("axisGridStart", NullValueHandling = NullValueHandling.Ignore)]
        public GridReference AxisGridStart { get; set; }

        /// <summary>
        ///     Axis grid reference at the end (null when unresolved).
        /// </summary>
        [JsonProperty("axisGridEnd", NullValueHandling = NullValueHandling.Ignore)]
        public GridReference AxisGridEnd { get; set; }

        /// <summary>
        ///     "From" grid reference at the start side (null when unresolved).
        /// </summary>
        [JsonProperty("fromGrid", NullValueHandling = NullValueHandling.Ignore)]
        public GridReference FromGrid { get; set; }

        /// <summary>
        ///     "To" grid reference at the end side (null when unresolved).
        /// </summary>
        [JsonProperty("toGrid", NullValueHandling = NullValueHandling.Ignore)]
        public GridReference ToGrid { get; set; }

        /// <summary>
        ///     Centreline length along the physical location curve (millimetres).
        /// </summary>
        [JsonProperty("centerlineLengthMm")]
        public double CenterlineLengthMm { get; set; }

        /// <summary>
        ///     Plan-projected length of the beam (millimetres). May differ from
        ///     <see cref="CenterlineLengthMm"/> when the beam is sloped.
        /// </summary>
        [JsonProperty("projectedLengthMm")]
        public double ProjectedLengthMm { get; set; }

        /// <summary>
        ///     Support evidence at the start of the beam.
        /// </summary>
        [JsonProperty("supportStart", NullValueHandling = NullValueHandling.Ignore)]
        public BeamSupportRecord SupportStart { get; set; }

        /// <summary>
        ///     Support evidence at the end of the beam.
        /// </summary>
        [JsonProperty("supportEnd", NullValueHandling = NullValueHandling.Ignore)]
        public BeamSupportRecord SupportEnd { get; set; }

        /// <summary>
        ///     Face-to-face clear span in millimetres. Null when either support
        ///     face cannot be established or the geometry indicates a cantilever.
        /// </summary>
        [JsonProperty("clearSpanMm", NullValueHandling = NullValueHandling.Ignore)]
        public double? ClearSpanMm { get; set; }

        /// <summary>
        ///     Free-form note for the row.
        /// </summary>
        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }

        /// <summary>
        ///     Derivation of the support selection.
        /// </summary>
        [JsonProperty("supportsDerivation", NullValueHandling = NullValueHandling.Ignore)]
        public DerivationInfo SupportsDerivation { get; set; }
    }

    /// <summary>
    ///     Beam location curve in 3D (millimetres). Curves retain their native
    ///     kind so the response does not lose information when projecting to plan.
    /// </summary>
    public class BeamLocationCurveDto
    {
        [JsonProperty("curveKind")]
        public CurveKind CurveKind { get; set; } = CurveKind.Line;

        [JsonProperty("startMm")]
        public PlanPoint2D StartMm { get; set; } = new PlanPoint2D();

        [JsonProperty("endMm")]
        public PlanPoint2D EndMm { get; set; } = new PlanPoint2D();

        [JsonProperty("startZElevationMm")]
        public double StartZElevationMm { get; set; }

        [JsonProperty("endZElevationMm")]
        public double EndZElevationMm { get; set; }
    }

    /// <summary>
    ///     Support evidence at one end of a beam. Includes the candidate ranking
    ///     context and the face station selected for clear-span computation.
    /// </summary>
    public class BeamSupportRecord
    {
        /// <summary>
        ///     Null when no valid inward support face was found within tolerance.
        /// </summary>
        [JsonProperty("supportElementId", NullValueHandling = NullValueHandling.Ignore)]
        public long? SupportElementId { get; set; }

        [JsonProperty("supportUniqueId", NullValueHandling = NullValueHandling.Ignore)]
        public string SupportUniqueId { get; set; }

        [JsonProperty("supportMark", NullValueHandling = NullValueHandling.Ignore)]
        public string SupportMark { get; set; }

        [JsonProperty("supportCategory")]
        public SupportCategory SupportCategory { get; set; } = SupportCategory.Unknown;

        /// <summary>
        ///     Contact face station along the beam axis, in millimetres from the
        ///     beam start. Null when no contact face was established.
        /// </summary>
        [JsonProperty("contactStationMm", NullValueHandling = NullValueHandling.Ignore)]
        public double? ContactStationMm { get; set; }

        /// <summary>
        ///     Plan coordinate of the contact face, in millimetres.
        /// </summary>
        [JsonProperty("contactPointMm", NullValueHandling = NullValueHandling.Ignore)]
        public PlanPoint2D ContactPointMm { get; set; }

        /// <summary>
        ///     How the contact face was determined.
        /// </summary>
        [JsonProperty("intersectionMethod")]
        public SupportIntersectionMethod IntersectionMethod { get; set; } = SupportIntersectionMethod.Unknown;

        /// <summary>
        ///     Confidence in [0, 1].
        /// </summary>
        [JsonProperty("confidence")]
        public double Confidence { get; set; }

        /// <summary>
        ///     True when the end has no inward support (cantilever / free end).
        /// </summary>
        [JsonProperty("isCantilever")]
        public bool IsCantilever { get; set; }

        /// <summary>
        ///     Ranking details for the chosen candidate and the runners-up.
        /// </summary>
        [JsonProperty("rankingEvidence", NullValueHandling = NullValueHandling.Ignore)]
        public SupportRankingEvidence RankingEvidence { get; set; }
    }

    /// <summary>
    ///     Evidence of the candidate ranking used to select a support face.
    ///     Returned when <see cref="FilterSummary.IncludeEvidence"/> is true.
    /// </summary>
    public class SupportRankingEvidence
    {
        [JsonProperty("searchRadiusMm")]
        public double SearchRadiusMm { get; set; }

        [JsonProperty("candidatesConsidered")]
        public int CandidatesConsidered { get; set; }

        /// <summary>
        ///     Top candidates, ordered by score ascending (best first). Limited
        ///     to a small number to keep the response compact.
        /// </summary>
        [JsonProperty("topCandidates", NullValueHandling = NullValueHandling.Ignore)]
        public List<SupportCandidateDto> TopCandidates { get; set; }
    }

    /// <summary>
    ///     Per-candidate evidence row in the ranking evidence list.
    /// </summary>
    public class SupportCandidateDto
    {
        [JsonProperty("supportElementId")]
        public long SupportElementId { get; set; }

        [JsonProperty("supportUniqueId")]
        public string SupportUniqueId { get; set; }

        [JsonProperty("supportCategory")]
        public SupportCategory SupportCategory { get; set; } = SupportCategory.Unknown;

        [JsonProperty("distanceMm")]
        public double DistanceMm { get; set; }

        [JsonProperty("categoryRank")]
        public int CategoryRank { get; set; }

        [JsonProperty("totalScore")]
        public double TotalScore { get; set; }

        [JsonProperty("accepted")]
        public bool Accepted { get; set; }

        [JsonProperty("rejectionReason", NullValueHandling = NullValueHandling.Ignore)]
        public string RejectionReason { get; set; }
    }
}
