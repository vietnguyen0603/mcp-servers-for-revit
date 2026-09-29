using System;

namespace RegisterGeometry
{
    /// <summary>
    ///     Opaque paging cursor. Carries the schema version, document key,
    ///     filter hash, and the last record key so the next page can be
    ///     fetched deterministically.
    /// </summary>
    public readonly struct Cursor : IEquatable<Cursor>
    {
        public Cursor(string schemaVersion, string documentKey, string filterHash, string lastRecordKey, int pageNumber)
        {
            SchemaVersion = schemaVersion ?? throw new ArgumentNullException(nameof(schemaVersion));
            DocumentKey = documentKey ?? throw new ArgumentNullException(nameof(documentKey));
            FilterHash = filterHash ?? throw new ArgumentNullException(nameof(filterHash));
            LastRecordKey = lastRecordKey ?? string.Empty;
            PageNumber = pageNumber;
        }

        public string SchemaVersion { get; }

        public string DocumentKey { get; }

        public string FilterHash { get; }

        /// <summary>
        ///     Opaque record key from the previous page, used to seek forward.
        /// </summary>
        public string LastRecordKey { get; }

        public int PageNumber { get; }

        public bool Equals(Cursor other) =>
            SchemaVersion == other.SchemaVersion &&
            DocumentKey == other.DocumentKey &&
            FilterHash == other.FilterHash &&
            LastRecordKey == other.LastRecordKey &&
            PageNumber == other.PageNumber;

        public override bool Equals(object? obj) => obj is Cursor other && Equals(other);

        public override int GetHashCode() => HashUtility.Combine(
            HashUtility.Of(SchemaVersion), HashUtility.Of(DocumentKey),
            HashUtility.Of(FilterHash), HashUtility.Of(LastRecordKey),
            HashUtility.Of(PageNumber));

        public override string ToString() =>
            $"Cursor(page={PageNumber}, doc={DocumentKey}, filter={ShortHash(FilterHash)}, last={LastRecordKey})";

        private static string ShortHash(string s) => s.Length <= 8 ? s : s.Substring(0, 8);
    }
}
