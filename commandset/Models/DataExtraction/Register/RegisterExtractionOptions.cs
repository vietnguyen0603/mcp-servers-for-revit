using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction.Register
{
    /// <summary>
    ///     Request options shared by every register extraction command. The DTO is
    ///     mirrored 1:1 in TypeScript so Zod and Newtonsoft serialisation agree.
    ///     Defaults are applied by the validator, not by the wire format, so that
    ///     clients can omit them and still get the documented behaviour.
    /// </summary>
    public class RegisterExtractionOptions
    {
        /// <summary>
        ///     Restrict collection to specific levels by id. Ambiguous or unknown
        ///     ids produce a validation error.
        /// </summary>
        [JsonProperty("levelIds", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> LevelIds { get; set; }

        /// <summary>
        ///     Restrict collection to levels by name. Ambiguous matches are an error.
        /// </summary>
        [JsonProperty("levelNames", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> LevelNames { get; set; }

        /// <summary>
        ///     Optional view id; when present the collector restricts to elements
        ///     visible in the view, otherwise the whole document is collected.
        /// </summary>
        [JsonProperty("viewId", NullValueHandling = NullValueHandling.Ignore)]
        public string ViewId { get; set; }

        /// <summary>
        ///     Optional explicit phase id; when omitted the active view phase is
        ///     used for view-scoped requests and the document default otherwise.
        /// </summary>
        [JsonProperty("phaseId", NullValueHandling = NullValueHandling.Ignore)]
        public string PhaseId { get; set; }

        /// <summary>
        ///     Design option policy. Default is <see cref="DesignOptionPolicy.Primary"/>.
        /// </summary>
        [JsonProperty("designOptionPolicy", NullValueHandling = NullValueHandling.Ignore)]
        public DesignOptionPolicy? DesignOptionPolicy { get; set; }

        /// <summary>
        ///     Whether to include elements from linked models. Default false.
        /// </summary>
        [JsonProperty("includeLinkedModels", NullValueHandling = NullValueHandling.Ignore)]
        public bool? IncludeLinkedModels { get; set; }

        /// <summary>
        ///     Coordinate frame for all plan geometry. Default <see cref="CoordinateSystem.Project"/>.
        /// </summary>
        [JsonProperty("coordinateSystem", NullValueHandling = NullValueHandling.Ignore)]
        public CoordinateSystem? CoordinateSystem { get; set; }

        /// <summary>
        ///     Maximum number of records per page. Default 500; hard-capped to
        ///     protect the unframed TCP response.
        /// </summary>
        [JsonProperty("pageSize", NullValueHandling = NullValueHandling.Ignore)]
        public int? PageSize { get; set; }

        /// <summary>
        ///     Opaque cursor returned from a previous page. Null for the first page.
        /// </summary>
        [JsonProperty("cursor", NullValueHandling = NullValueHandling.Ignore)]
        public string Cursor { get; set; }

        /// <summary>
        ///     When false the response omits per-record evidence blocks to keep
        ///     rows compact. Default true.
        /// </summary>
        [JsonProperty("includeEvidence", NullValueHandling = NullValueHandling.Ignore)]
        public bool? IncludeEvidence { get; set; }

        /// <summary>
        ///     Tolerance bounds for derivation. Defaults applied when null.
        /// </summary>
        [JsonProperty("tolerances", NullValueHandling = NullValueHandling.Ignore)]
        public ToleranceSettingsDto Tolerances { get; set; }

        /// <summary>
        ///     Ordered parameter aliases keyed by a logical name (e.g. "mark",
        ///     "width", "depth"). Built-in parameters are tried before aliases.
        /// </summary>
        [JsonProperty("parameterMap", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, List<string>> ParameterMap { get; set; }
    }

    /// <summary>
    ///     Options specific to the grid register command.
    /// </summary>
    public class GridRegisterExtractionOptions : RegisterExtractionOptions
    {
        /// <summary>
        ///     Unique id of the grid that should be considered coordinate zero.
        ///     When null, <see cref="OriginGridName"/> is consulted.
        /// </summary>
        [JsonProperty("originGridUniqueId", NullValueHandling = NullValueHandling.Ignore)]
        public string OriginGridUniqueId { get; set; }

        /// <summary>
        ///     Name of the origin grid; matched exactly once. Duplicate names are
        ///     rejected with an error.
        /// </summary>
        [JsonProperty("originGridName", NullValueHandling = NullValueHandling.Ignore)]
        public string OriginGridName { get; set; }

        /// <summary>
        ///     Explicit axis-family assignment keyed by either a Revit
        ///     <c>UniqueId</c> or a grid display name. The parser populates this
        ///     dictionary from the
        ///     <c>{ axisFamily, gridNames: [...], uniqueId? }</c> array on the
        ///     wire: every entry in <c>gridNames</c> (and the optional
        ///     <c>uniqueId</c> when supplied) becomes a key with the family's
        ///     value. Use this to force a particular grid onto X or Y when
        ///     automatic clustering is ambiguous; duplicate or conflicting
        ///     entries are warned about and the first assignment wins.
        /// </summary>
        [JsonProperty("axisAssignments", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, AxisFamily> AxisAssignments { get; set; }
    }

    /// <summary>
    ///     Options specific to the column/wall register command.
    /// </summary>
    public class ColumnWallRegisterExtractionOptions : RegisterExtractionOptions
    {
        /// <summary>
        ///     When true, architectural columns (OST_Columns) are included in
        ///     addition to structural columns. Default false.
        /// </summary>
        [JsonProperty("includeArchitecturalColumns", NullValueHandling = NullValueHandling.Ignore)]
        public bool? IncludeArchitecturalColumns { get; set; }

        /// <summary>
        ///     When true, stacked walls are reported as a single record per leg;
        ///     when false each stacked wall is reported separately. Default false.
        /// </summary>
        [JsonProperty("mergeStackedWalls", NullValueHandling = NullValueHandling.Ignore)]
        public bool? MergeStackedWalls { get; set; }

        /// <summary>
        ///     Maximum span between candidate wall legs to consider them connected
        ///     when grouping. Defaults to <see cref="ToleranceSettingsDto.GroupingMm"/>.
        /// </summary>
        [JsonProperty("maxWallGroupSpanMm", NullValueHandling = NullValueHandling.Ignore)]
        public double? MaxWallGroupSpanMm { get; set; }

        /// <summary>
        ///     Optional project mark prefixes that identify multi-leg core/shear
        ///     walls (for example <c>"CW"</c>, <c>"SW"</c>). The handler uses
        ///     the prefixes as grouping evidence for legs that lack explicit
        ///     model grouping; legs are never merged solely because they share
        ///     the same mark. At most 64 prefixes, each 1-32 characters.
        /// </summary>
        [JsonProperty("corePrefixes", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> CorePrefixes { get; set; }
    }

    /// <summary>
    ///     Options specific to the beam register command.
    /// </summary>
    public class BeamRegisterExtractionOptions : RegisterExtractionOptions
    {
        /// <summary>
        ///     Category precedence for ties when two support candidates are
        ///     geometrically equivalent. Defaults to wall then column then beam.
        /// </summary>
        [JsonProperty("supportCategoryPrecedence", NullValueHandling = NullValueHandling.Ignore)]
        public List<SupportCategory> SupportCategoryPrecedence { get; set; }

        /// <summary>
        ///     Minimum confidence required to report a clear span. Defaults to 0.5.
        /// </summary>
        [JsonProperty("minClearSpanConfidence", NullValueHandling = NullValueHandling.Ignore)]
        public double? MinClearSpanConfidence { get; set; }

        /// <summary>
        ///     Include cantilevers as records (beam end without inward support
        ///     face). Default true.
        /// </summary>
        [JsonProperty("includeCantilevers", NullValueHandling = NullValueHandling.Ignore)]
        public bool? IncludeCantilevers { get; set; }

        /// <summary>
        ///     Optional project classification aliases keyed by a logical
        ///     classification label (e.g. "HB", "VB", "SP", "B"). The handler
        ///     resolves the beam's <c>classification</c> field by walking the
        ///     aliases for each label and matching the candidate parameter by
        ///     built-in enum first, then by name. Keys and aliases are
        ///     case-sensitive. Mirrors the shape of <c>parameterMap</c>.
        /// </summary>
        [JsonProperty("beamTypeAliases", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, List<string>> BeamTypeAliases { get; set; }
    }
}
