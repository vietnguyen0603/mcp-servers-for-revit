using System;
using RegisterGeometry;

namespace RevitMCPCommandSet.Services.DataExtraction.Register
{
    /// <summary>
    ///     Decodes opaque cursor strings produced by the previous page and
    ///     validates them against the live filter hash, document key, and
    ///     schema version. The validator returns a structured rejection
    ///     reason that callers can surface as a warning entry.
    /// </summary>
    /// <remarks>
    ///     The format is owned by <see cref="CursorCodec"/> in the pure
    ///     geometry library; this wrapper layers the per-handler invariants
    ///     on top of it so handlers cannot bypass the checks.
    /// </remarks>
    public static class CursorValidator
    {
        /// <summary>
        ///     Schema version this build emits. Bumped on breaking changes.
        ///     Mirrors the version pinned on the DTO envelope.
        /// </summary>
        public const string CurrentSchemaVersion = "1.0";

        /// <summary>
        ///     Try to parse a cursor string. Returns the cursor on success and
        ///     a null rejection reason. On failure returns the default cursor
        ///     and a stable snake_case rejection code safe to surface.
        /// </summary>
        public static bool TryDecode(
            string encoded,
            string expectedFilterHash,
            string expectedDocumentKey,
            out Cursor cursor,
            out string rejectionReason)
        {
            cursor = default;
            rejectionReason = null;

            if (string.IsNullOrEmpty(encoded))
            {
                rejectionReason = "missing_cursor";
                return false;
            }

            Cursor decoded;
            try
            {
                decoded = CursorCodec.Decode(encoded);
            }
            catch (CursorFormatException ex)
            {
                rejectionReason = MapFormatException(ex);
                return false;
            }

            var reason = CursorCodec.Validate(
                decoded,
                expectedSchemaVersion: CurrentSchemaVersion,
                expectedFilterHash: expectedFilterHash,
                expectedDocumentKey: expectedDocumentKey);

            if (reason != null)
            {
                rejectionReason = reason;
                return false;
            }

            cursor = decoded;
            return true;
        }

        /// <summary>
        ///     Build a cursor for the next page from the previously-emitted
        ///     last record key.
        /// </summary>
        public static Cursor BuildNext(
            string documentKey,
            string filterHash,
            string lastRecordKey,
            int nextPageNumber)
        {
            if (nextPageNumber < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(nextPageNumber), "Page number must be non-negative.");
            }
            return new Cursor(
                CurrentSchemaVersion,
                documentKey ?? throw new ArgumentNullException(nameof(documentKey)),
                filterHash ?? throw new ArgumentNullException(nameof(filterHash)),
                lastRecordKey ?? string.Empty,
                nextPageNumber);
        }

        private static string MapFormatException(CursorFormatException ex)
        {
            var message = ex.Message ?? string.Empty;
            if (message.IndexOf("Cursor exceeds", StringComparison.Ordinal) >= 0)
            {
                return "cursor_too_long";
            }
            if (message.IndexOf("base64", StringComparison.Ordinal) >= 0)
            {
                return "cursor_invalid_base64";
            }
            if (message.IndexOf("UTF-8", StringComparison.Ordinal) >= 0)
            {
                return "cursor_invalid_encoding";
            }
            if (message.IndexOf("fields", StringComparison.Ordinal) >= 0)
            {
                return "cursor_invalid_shape";
            }
            if (message.IndexOf("Page number", StringComparison.Ordinal) >= 0)
            {
                return "cursor_invalid_page";
            }
            if (message.IndexOf("Schema version", StringComparison.Ordinal) >= 0
                || message.IndexOf("Document key", StringComparison.Ordinal) >= 0
                || message.IndexOf("Filter hash", StringComparison.Ordinal) >= 0)
            {
                return "cursor_invalid_field";
            }
            return "cursor_malformed";
        }
    }
}