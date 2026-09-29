using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction.Register
{
    /// <summary>
    ///     Structured warning entry. Severity governs how clients surface the
    ///     item; <see cref="Code"/> is a stable snake_case identifier that
    ///     tests can match on.
    /// </summary>
    public class WarningEntry
    {
        /// <summary>
        ///     Stable snake_case identifier (e.g. "missing_origin",
        ///     "ambiguous_level_name", "unsupported_curve_kind").
        /// </summary>
        [JsonProperty("code")]
        public string Code { get; set; }

        /// <summary>
        ///     Severity bucket for the warning.
        /// </summary>
        [JsonProperty("severity")]
        public WarningSeverity Severity { get; set; } = WarningSeverity.Warning;

        /// <summary>
        ///     Human-readable description, safe to surface to the user.
        /// </summary>
        [JsonProperty("message")]
        public string Message { get; set; }

        /// <summary>
        ///     Optional element identifier (string form) this warning pertains to.
        /// </summary>
        [JsonProperty("elementId", NullValueHandling = NullValueHandling.Ignore)]
        public string ElementId { get; set; }

        /// <summary>
        ///     Optional document key when the warning originates from a linked
        ///     model record.
        /// </summary>
        [JsonProperty("documentKey", NullValueHandling = NullValueHandling.Ignore)]
        public string DocumentKey { get; set; }

        /// <summary>
        ///     Optional structured payload (e.g. conflicting origin candidates).
        /// </summary>
        [JsonProperty("detail", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, object> Detail { get; set; }
    }
}
