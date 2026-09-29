using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction.Register
{
    /// <summary>
    ///     Echo of the applied request filters. Serialised so that clients can
    ///     confirm what the handler actually received, especially after the
    ///     validator has clamped invalid values back to defaults.
    /// </summary>
    public class FilterSummary
    {
        /// <summary>
        ///     Level ids that were honoured.
        /// </summary>
        [JsonProperty("levelIds", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> LevelIds { get; set; }

        /// <summary>
        ///     Level names that were honoured. Ambiguous or unresolvable names
        ///     are dropped and recorded as warnings.
        /// </summary>
        [JsonProperty("levelNames", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> LevelNames { get; set; }

        /// <summary>
        ///     View id when the request was view-scoped.
        /// </summary>
        [JsonProperty("viewId", NullValueHandling = NullValueHandling.Ignore)]
        public string ViewId { get; set; }

        /// <summary>
        ///     Phase id applied by the handler.
        /// </summary>
        [JsonProperty("phaseId", NullValueHandling = NullValueHandling.Ignore)]
        public string PhaseId { get; set; }

        /// <summary>
        ///     Design option policy actually applied.
        /// </summary>
        [JsonProperty("designOptionPolicy")]
        public DesignOptionPolicy DesignOptionPolicy { get; set; } = DesignOptionPolicy.Primary;

        /// <summary>
        ///     Whether linked models were collected.
        /// </summary>
        [JsonProperty("includeLinkedModels")]
        public bool IncludeLinkedModels { get; set; }

        /// <summary>
        ///     Coordinate system used for plan geometry.
        /// </summary>
        [JsonProperty("coordinateSystem")]
        public CoordinateSystem CoordinateSystem { get; set; } = CoordinateSystem.Project;

        /// <summary>
        ///     Whether the response includes per-record evidence blocks.
        /// </summary>
        [JsonProperty("includeEvidence")]
        public bool IncludeEvidence { get; set; } = true;

        /// <summary>
        ///     Resolved tolerances after validation.
        /// </summary>
        [JsonProperty("tolerances", NullValueHandling = NullValueHandling.Ignore)]
        public ToleranceSettingsDto Tolerances { get; set; }

        /// <summary>
        ///     Stable hash of the applied filters; embedded into cursors so that
        ///     stale cursors can be rejected.
        /// </summary>
        [JsonProperty("filterHash")]
        public string FilterHash { get; set; }
    }

    /// <summary>
    ///     Tolerance bounds applied to derivation steps. All values are millimetres
    ///     except <see cref="AngularDegrees"/>, which is in degrees.
    /// </summary>
    public class ToleranceSettingsDto
    {
        [JsonProperty("angularDegrees")]
        public double AngularDegrees { get; set; } = 1.0;

        [JsonProperty("intersectionMm")]
        public double IntersectionMm { get; set; } = 1.0;

        [JsonProperty("groupingMm")]
        public double GroupingMm { get; set; } = 50.0;

        [JsonProperty("supportSearchMm")]
        public double SupportSearchMm { get; set; } = 300.0;

        [JsonProperty("snapMm")]
        public double SnapMm { get; set; } = 1.0;
    }
}
