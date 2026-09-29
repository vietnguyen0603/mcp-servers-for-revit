using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction.Register
{
    /// <summary>
    ///     One record per physical Revit column or wall leg. Columns and wall
    ///     legs share a single envelope so consumers can iterate the union and
    ///     switch on <see cref="RecordKind"/>.
    /// </summary>
    public class ColumnWallRecord
    {
        /// <summary>
        ///     Stable provenance block.
        /// </summary>
        [JsonProperty("provenance")]
        public ElementProvenance Provenance { get; set; } = new ElementProvenance();

        /// <summary>
        ///     Discriminator between columns and wall legs.
        /// </summary>
        [JsonProperty("recordKind")]
        public ColumnWallRecordKind RecordKind { get; set; } = ColumnWallRecordKind.Column;

        /// <summary>
        ///     Element mark, resolved from configured parameter aliases. Null
        ///     when the element has no mark.
        /// </summary>
        [JsonProperty("mark", NullValueHandling = NullValueHandling.Ignore)]
        public string Mark { get; set; }

        /// <summary>
        ///     Project classification (e.g. CW, SW, RC). Null when not derived.
        /// </summary>
        [JsonProperty("classification", NullValueHandling = NullValueHandling.Ignore)]
        public string Classification { get; set; }

        /// <summary>
        ///     Plan shape of this record. Walls inherit their group shape when
        ///     grouped; columns and isolated legs report <see cref="WallShape.Planar"/>.
        /// </summary>
        [JsonProperty("shape")]
        public WallShape Shape { get; set; } = WallShape.Unknown;

        /// <summary>
        ///     1-based leg number within its group; 1 for columns and isolated walls.
        /// </summary>
        [JsonProperty("legNumber")]
        public int LegNumber { get; set; } = 1;

        /// <summary>
        ///     Total legs in the group; 1 for columns and isolated walls.
        /// </summary>
        [JsonProperty("legCount")]
        public int LegCount { get; set; } = 1;

        /// <summary>
        ///     Plan rotation in degrees, measured from the local +X axis.
        /// </summary>
        [JsonProperty("rotationDeg")]
        public double RotationDeg { get; set; }

        /// <summary>
        ///     Reference level name for the element.
        /// </summary>
        [JsonProperty("levelName", NullValueHandling = NullValueHandling.Ignore)]
        public string LevelName { get; set; }

        /// <summary>
        ///     Base offset from the reference level (millimetres).
        /// </summary>
        [JsonProperty("baseOffsetMm")]
        public double BaseOffsetMm { get; set; }

        /// <summary>
        ///     Top offset from the reference level (millimetres).
        /// </summary>
        [JsonProperty("topOffsetMm")]
        public double TopOffsetMm { get; set; }

        /// <summary>
        ///     Plan footprint extent in millimetres along its local axes.
        /// </summary>
        [JsonProperty("dxMm")]
        public double DxMm { get; set; }

        [JsonProperty("dyMm")]
        public double DyMm { get; set; }

        /// <summary>
        ///     Nearest X grid reference (null when no X family exists).
        /// </summary>
        [JsonProperty("nearestGridX", NullValueHandling = NullValueHandling.Ignore)]
        public GridReference NearestGridX { get; set; }

        /// <summary>
        ///     Nearest Y grid reference (null when no Y family exists).
        /// </summary>
        [JsonProperty("nearestGridY", NullValueHandling = NullValueHandling.Ignore)]
        public GridReference NearestGridY { get; set; }

        /// <summary>
        ///     Signed offset to the left footprint face in millimetres (null
        ///     when there is no scalar X family).
        /// </summary>
        [JsonProperty("offsetLeftMm", NullValueHandling = NullValueHandling.Ignore)]
        public double? OffsetLeftMm { get; set; }

        /// <summary>
        ///     Signed offset to the bottom footprint face in millimetres (null
        ///     when there is no scalar Y family).
        /// </summary>
        [JsonProperty("offsetBottomMm", NullValueHandling = NullValueHandling.Ignore)]
        public double? OffsetBottomMm { get; set; }

        /// <summary>
        ///     Axis-aligned bounding box in plan, in millimetres. Available even
        ///     when the record has no scalar grid references.
        /// </summary>
        [JsonProperty("extentsMm")]
        public BoundingBox2D ExtentsMm { get; set; } = new BoundingBox2D();

        /// <summary>
        ///     Centre point of the footprint in plan, in millimetres.
        /// </summary>
        [JsonProperty("centrePointMm")]
        public PlanPoint2D CentrePointMm { get; set; } = new PlanPoint2D();

        /// <summary>
        ///     Free-form note for the row.
        /// </summary>
        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }

        /// <summary>
        ///     Group metadata when this record is part of a wall group. Null for
        ///     columns and isolated wall legs.
        /// </summary>
        [JsonProperty("wallGroup", NullValueHandling = NullValueHandling.Ignore)]
        public WallGroupInfo WallGroup { get; set; }

        /// <summary>
        ///     Provenance for how dimensions were derived.
        /// </summary>
        [JsonProperty("dimensionsDerivation", NullValueHandling = NullValueHandling.Ignore)]
        public DerivationInfo DimensionsDerivation { get; set; }
    }

    /// <summary>
    ///     Reference to a nearby grid (used for nearest-grid associations).
    /// </summary>
    public class GridReference
    {
        [JsonProperty("gridUniqueId")]
        public string GridUniqueId { get; set; }

        [JsonProperty("gridName")]
        public string GridName { get; set; }

        /// <summary>
        ///     Signed offset from the record centre to the grid line, in millimetres.
        /// </summary>
        [JsonProperty("signedOffsetMm")]
        public double SignedOffsetMm { get; set; }

        /// <summary>
        ///     Plan distance from the record centre to the grid line, in millimetres.
        /// </summary>
        [JsonProperty("distanceMm")]
        public double DistanceMm { get; set; }
    }

    /// <summary>
    ///     Axis-aligned bounding box in plan, in millimetres.
    /// </summary>
    public class BoundingBox2D
    {
        [JsonProperty("minMm")]
        public PlanPoint2D MinMm { get; set; } = new PlanPoint2D();

        [JsonProperty("maxMm")]
        public PlanPoint2D MaxMm { get; set; } = new PlanPoint2D();
    }

    /// <summary>
    ///     Wall grouping metadata. Walls are grouped by explicit model grouping
    ///     first, then configured mark plus spatial connectivity, then by
    ///     confidence-only heuristics.
    /// </summary>
    public class WallGroupInfo
    {
        /// <summary>
        ///     Stable id of the group (host:uniqueId:origin or hash).
        /// </summary>
        [JsonProperty("groupId")]
        public string GroupId { get; set; }

        /// <summary>
        ///     User-visible label for the group (e.g. "CW-1").
        /// </summary>
        [JsonProperty("groupLabel", NullValueHandling = NullValueHandling.Ignore)]
        public string GroupLabel { get; set; }

        /// <summary>
        ///     Shape classification of the grouped core.
        /// </summary>
        [JsonProperty("shape")]
        public WallShape Shape { get; set; } = WallShape.Unknown;

        /// <summary>
        ///     1-based index of this leg in the group.
        /// </summary>
        [JsonProperty("legNumber")]
        public int LegNumber { get; set; } = 1;

        /// <summary>
        ///     Total legs in the group.
        /// </summary>
        [JsonProperty("legCount")]
        public int LegCount { get; set; } = 1;

        /// <summary>
        ///     Confidence of the grouping decision in [0, 1].
        /// </summary>
        [JsonProperty("groupingConfidence")]
        public double GroupingConfidence { get; set; } = 1.0;

        /// <summary>
        ///     Method used to establish grouping.
        /// </summary>
        [JsonProperty("groupingMethod")]
        public string GroupingMethod { get; set; }
    }
}
