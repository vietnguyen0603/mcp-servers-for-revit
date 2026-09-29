using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction.Register
{
    /// <summary>
    ///     Document/extraction snapshot metadata embedded in every response.
    ///     <see cref="DocumentPathHash"/> is a one-way digest of the full local
    ///     document path so that two extractions can be correlated without
    ///     exposing the absolute path.
    /// </summary>
    public class RegisterSnapshot
    {
        /// <summary>
        ///     Document title (no path). Null when the document has not been saved.
        /// </summary>
        [JsonProperty("documentTitle")]
        public string DocumentTitle { get; set; }

        /// <summary>
        ///     Stable SHA-256-like hex digest of the absolute document path.
        /// </summary>
        [JsonProperty("documentPathHash")]
        public string DocumentPathHash { get; set; }

        /// <summary>
        ///     Host Revit product version string (e.g. "2025.3").
        /// </summary>
        [JsonProperty("revitVersion")]
        public string RevitVersion { get; set; }

        /// <summary>
        ///     UTC instant at which the extraction started (ISO-8601).
        /// </summary>
        [JsonProperty("extractedAtUtc")]
        public string ExtractedAtUtc { get; set; }

        /// <summary>
        ///     Coordinate frame used for all plan geometry in the response.
        /// </summary>
        [JsonProperty("coordinateSystem")]
        public CoordinateSystem CoordinateSystem { get; set; } = CoordinateSystem.Project;

        /// <summary>
        ///     Linear unit used for all geometry fields. Always "mm" for now.
        /// </summary>
        [JsonProperty("lengthUnit")]
        public string LengthUnit { get; set; } = "mm";

        /// <summary>
        ///     Active phase id resolved by the extractor (null if not applied).
        /// </summary>
        [JsonProperty("phaseId")]
        public string PhaseId { get; set; }

        /// <summary>
        ///     Active design option id resolved by the extractor (null when the
        ///     policy is <see cref="DesignOptionPolicy.All"/>).
        /// </summary>
        [JsonProperty("designOptionId")]
        public string DesignOptionId { get; set; }

        /// <summary>
        ///     Project location name used to derive coordinates. Null when not
        ///     applicable to the selected frame.
        /// </summary>
        [JsonProperty("projectLocationName")]
        public string ProjectLocationName { get; set; }
    }
}
