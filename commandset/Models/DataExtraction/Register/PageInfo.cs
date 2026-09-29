using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction.Register
{
    /// <summary>
    ///     Paging metadata. When <see cref="HasMore"/> is true, <see cref="NextCursor"/>
    ///     must be passed verbatim to obtain the next page; cursors are opaque,
    ///     versioned, and bound to the request filters.
    /// </summary>
    public class PageInfo
    {
        /// <summary>
        ///     Opaque cursor for the next page; null when this is the last page.
        /// </summary>
        [JsonProperty("nextCursor", NullValueHandling = NullValueHandling.Ignore)]
        public string NextCursor { get; set; }

        /// <summary>
        ///     Number of records returned in this page (not the total).
        /// </summary>
        [JsonProperty("returned")]
        public int Returned { get; set; }

        /// <summary>
        ///     Total records discovered before paging. Null when paging is
        ///     unnecessary or the document is too large to count cheaply.
        /// </summary>
        [JsonProperty("totalEstimate", NullValueHandling = NullValueHandling.Ignore)]
        public int? TotalEstimate { get; set; }

        /// <summary>
        ///     True when another page exists.
        /// </summary>
        [JsonProperty("hasMore")]
        public bool HasMore { get; set; }
    }
}
