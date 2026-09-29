using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction.Register
{
    /// <summary>
    ///     Identity and provenance block embedded on every register record.
    ///     Carries Revit element identity plus the document context and any
    ///     record-scoped warnings, so that downstream code can audit each row.
    /// </summary>
    public class ElementProvenance
    {
        /// <summary>
        ///     Revit integer element id of the source instance.
        /// </summary>
        [JsonProperty("elementId")]
        public long ElementId { get; set; }

        /// <summary>
        ///     Stable Revit unique id; never reused across sessions.
        /// </summary>
        [JsonProperty("uniqueId")]
        public string UniqueId { get; set; }

        /// <summary>
        ///     Stable key identifying the source document. For the host document
        ///     this is "host"; for linked instances it is a deterministic hash
        ///     of the link instance identity so that records remain self-locating.
        /// </summary>
        [JsonProperty("documentKey")]
        public string DocumentKey { get; set; }

        /// <summary>
        ///     Unique id of the Revit link instance that contains the record, or
        ///     null when the record comes from the host document.
        /// </summary>
        [JsonProperty("linkInstanceUniqueId", NullValueHandling = NullValueHandling.Ignore)]
        public string LinkInstanceUniqueId { get; set; }

        /// <summary>
        ///     Resolved level id when the record was level-scoped.
        /// </summary>
        [JsonProperty("levelId", NullValueHandling = NullValueHandling.Ignore)]
        public string LevelId { get; set; }

        /// <summary>
        ///     Resolved level name; null when the element is not level-bound.
        /// </summary>
        [JsonProperty("levelName", NullValueHandling = NullValueHandling.Ignore)]
        public string LevelName { get; set; }

        /// <summary>
        ///     Resolved phase id (null when the document has no phases).
        /// </summary>
        [JsonProperty("phaseId", NullValueHandling = NullValueHandling.Ignore)]
        public string PhaseId { get; set; }

        /// <summary>
        ///     Design option id of the element; null when the element is in the
        ///     main model or design options are not active.
        /// </summary>
        [JsonProperty("designOptionId", NullValueHandling = NullValueHandling.Ignore)]
        public string DesignOptionId { get; set; }

        /// <summary>
        ///     Family name when the record is instance-based (null for grids).
        /// </summary>
        [JsonProperty("familyName", NullValueHandling = NullValueHandling.Ignore)]
        public string FamilyName { get; set; }

        /// <summary>
        ///     Family type / symbol name when the record is instance-based.
        /// </summary>
        [JsonProperty("typeName", NullValueHandling = NullValueHandling.Ignore)]
        public string TypeName { get; set; }

        /// <summary>
        ///     Category name as reported by Revit (e.g. "Structural Columns").
        /// </summary>
        [JsonProperty("category", NullValueHandling = NullValueHandling.Ignore)]
        public string Category { get; set; }

        /// <summary>
        ///     Per-record warnings; aggregation of these is also surfaced on the
        ///     response envelope so clients do not have to walk records.
        /// </summary>
        [JsonProperty("warnings", NullValueHandling = NullValueHandling.Ignore)]
        public List<WarningEntry> Warnings { get; set; }
    }

    /// <summary>
    ///     Describes how a derived field (e.g. nearest grid reference) was obtained
    ///     so downstream users know whether the value is parametric or geometric.
    /// </summary>
    public class DerivationInfo
    {
        /// <summary>
        ///     Stable snake_case derivation method (e.g. "geometry_extent",
        ///     "type_parameter", "nearest_grid_by_distance").
        /// </summary>
        [JsonProperty("method")]
        public string Method { get; set; }

        /// <summary>
        ///     Optional parameter name when the derivation reads a type or
        ///     instance parameter.
        /// </summary>
        [JsonProperty("sourceParameter", NullValueHandling = NullValueHandling.Ignore)]
        public string SourceParameter { get; set; }

        /// <summary>
        ///     Confidence in [0, 1]. Lower values indicate geometric fallback
        ///     or unresolved joins.
        /// </summary>
        [JsonProperty("confidence")]
        public double Confidence { get; set; } = 1.0;

        /// <summary>
        ///     Free-form note explaining the derivation outcome.
        /// </summary>
        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }
    }
}
