using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction.Register
{
    /// <summary>
    ///     One record per physical Revit grid. Skew and radial grids are kept
    ///     distinct records with <see cref="CoordinateMm"/> null and a warning
    ///     indicating the scalar X/Y representation is insufficient.
    /// </summary>
    public class GridRecord
    {
        /// <summary>
        ///     Stable provenance block.
        /// </summary>
        [JsonProperty("provenance")]
        public ElementProvenance Provenance { get; set; } = new ElementProvenance();

        /// <summary>
        ///     Display name of the grid as configured in Revit. Primed names are
        ///     preserved verbatim (e.g. "A'").
        /// </summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>
        ///     Axis family the grid belongs to after clustering.
        /// </summary>
        [JsonProperty("axisFamily")]
        public AxisFamily AxisFamily { get; set; } = AxisFamily.Unknown;

        /// <summary>
        ///     Plan curve kind for the grid's geometry in the chosen frame.
        /// </summary>
        [JsonProperty("curveKind")]
        public CurveKind CurveKind { get; set; } = CurveKind.Line;

        /// <summary>
        ///     Start point of the grid curve in plan (millimetres).
        /// </summary>
        [JsonProperty("startPointMm")]
        public PlanPoint2D StartPointMm { get; set; } = new PlanPoint2D();

        /// <summary>
        ///     End point of the grid curve in plan (millimetres).
        /// </summary>
        [JsonProperty("endPointMm")]
        public PlanPoint2D EndPointMm { get; set; } = new PlanPoint2D();

        /// <summary>
        ///     Centre point and radius when the grid is an arc (millimetres).
        ///     Null for line grids.
        /// </summary>
        [JsonProperty("arcGeometry", NullValueHandling = NullValueHandling.Ignore)]
        public ArcGeometryDto ArcGeometry { get; set; }

        /// <summary>
        ///     Signed scalar coordinate in millimetres when the grid belongs to a
        ///     family that supports scalar positioning (X or Y around a chosen
        ///     origin). Null for skew, radial, or unresolvable grids.
        /// </summary>
        [JsonProperty("coordinateMm", NullValueHandling = NullValueHandling.Ignore)]
        public double? CoordinateMm { get; set; }

        /// <summary>
        ///     Distance to the previous grid in the same family, in millimetres.
        ///     Null when the grid is the first in its family or the previous grid
        ///     is unresolved.
        /// </summary>
        [JsonProperty("spacingMm", NullValueHandling = NullValueHandling.Ignore)]
        public double? SpacingMm { get; set; }

        /// <summary>
        ///     True when this grid is the configured origin (coordinate zero).
        ///     False when no origin was configured.
        /// </summary>
        [JsonProperty("isOrigin")]
        public bool IsOrigin { get; set; }

        /// <summary>
        ///     Register-ready direction label (e.g. "X", "Y", "SKEW", "RADIAL").
        /// </summary>
        [JsonProperty("direction")]
        public string Direction { get; set; }

        /// <summary>
        ///     Free-form note captured by the extractor.
        /// </summary>
        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }

        /// <summary>
        ///     Provenance for how the axis family was resolved.
        /// </summary>
        [JsonProperty("axisFamilyDerivation", NullValueHandling = NullValueHandling.Ignore)]
        public DerivationInfo AxisFamilyDerivation { get; set; }
    }

    /// <summary>
    ///     Planar point in the response envelope, expressed in millimetres.
    /// </summary>
    public class PlanPoint2D
    {
        [JsonProperty("x")]
        public double X { get; set; }

        [JsonProperty("y")]
        public double Y { get; set; }
    }

    /// <summary>
    ///     Arc geometry for an arc-typed grid record. Angles are in degrees.
    /// </summary>
    public class ArcGeometryDto
    {
        [JsonProperty("centerMm")]
        public PlanPoint2D CenterMm { get; set; } = new PlanPoint2D();

        [JsonProperty("radiusMm")]
        public double RadiusMm { get; set; }

        [JsonProperty("startAngleDeg")]
        public double StartAngleDeg { get; set; }

        [JsonProperty("endAngleDeg")]
        public double EndAngleDeg { get; set; }

        [JsonProperty("isFullCircle")]
        public bool IsFullCircle { get; set; }
    }
}
