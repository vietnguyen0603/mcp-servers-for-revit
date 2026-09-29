using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction.Register
{
    /// <summary>
    ///     Common envelope for every register extraction response. The versioned
    ///     <see cref="SchemaVersion"/> string allows clients to detect breaking
    ///     changes to the contract; minor additive changes preserve the major
    ///     version. Cursors and provenance are bound to this version.
    /// </summary>
    public class RegisterResponseEnvelope<TRecord>
    {
        /// <summary>
        ///     Stable schema identifier (e.g. "1.0"). Increments on breaking changes.
        /// </summary>
        [JsonProperty("schemaVersion")]
        public string SchemaVersion { get; set; } = "1.0";

        /// <summary>
        ///     Document/extract snapshot metadata. Identifies the source document
        ///     by a stable hash instead of exposing the full local path.
        /// </summary>
        [JsonProperty("snapshot")]
        public RegisterSnapshot Snapshot { get; set; } = new RegisterSnapshot();

        /// <summary>
        ///     Echo of the applied request filters, so the response is self-describing
        ///     even when intermediate handlers normalise the original request.
        /// </summary>
        [JsonProperty("filters")]
        public FilterSummary Filters { get; set; } = new FilterSummary();

        /// <summary>
        ///     Page payload. The records list only contains the current page.
        /// </summary>
        [JsonProperty("records")]
        public List<TRecord> Records { get; set; } = new List<TRecord>();

        /// <summary>
        ///     Aggregated warnings (whole-request). Per-record warnings live on
        ///     <see cref="ElementProvenance.Warnings"/>.
        /// </summary>
        [JsonProperty("warnings")]
        public List<WarningEntry> Warnings { get; set; } = new List<WarningEntry>();

        /// <summary>
        ///     Paging metadata. <see cref="PageInfo.HasMore"/> false means the page
        ///     is final; otherwise <see cref="PageInfo.NextCursor"/> must be passed
        ///     back verbatim to fetch the next page.
        /// </summary>
        [JsonProperty("page")]
        public PageInfo Page { get; set; } = new PageInfo();
    }
}
