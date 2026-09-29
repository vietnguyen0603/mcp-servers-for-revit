using System;
using System.Globalization;
using System.Text;

namespace RegisterGeometry
{
    /// <summary>
    ///     Encodes/decodes <see cref="Cursor"/> values into opaque, versioned,
    ///     URL-safe strings. The wire format is:
    ///     <c>base64url( "&lt;schemaVersion&gt;|&lt;documentKey&gt;|&lt;filterHash&gt;|&lt;lastRecordKey&gt;|&lt;pageNumber&gt;" )</c>
    ///     Decoding is strict: malformed input throws <see cref="CursorFormatException"/>.
    /// </summary>
    public static class CursorCodec
    {
        private const char Separator = '|';
        public const int MaxWireLength = 1024;

        /// <summary>
        ///     Encode a cursor to a URL-safe string. The result never exceeds
        ///     <see cref="MaxWireLength"/> for any input the handlers are
        ///     expected to produce.
        /// </summary>
        public static string Encode(Cursor cursor)
        {
            var sb = new StringBuilder();
            sb.Append(cursor.SchemaVersion);
            sb.Append(Separator);
            sb.Append(cursor.DocumentKey);
            sb.Append(Separator);
            sb.Append(cursor.FilterHash);
            sb.Append(Separator);
            sb.Append(cursor.LastRecordKey);
            sb.Append(Separator);
            sb.Append(cursor.PageNumber.ToString(CultureInfo.InvariantCulture));
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        /// <summary>
        ///     Decode a cursor string. Throws <see cref="CursorFormatException"/>
        ///     when the input is malformed or stale (e.g. unsupported schema
        ///     version or wrong field count).
        /// </summary>
        public static Cursor Decode(string encoded)
        {
            if (string.IsNullOrEmpty(encoded)) throw new CursorFormatException("Cursor is empty.");
            if (encoded.Length > MaxWireLength) throw new CursorFormatException("Cursor exceeds max wire length.");
            string base64 = encoded.Replace('-', '+').Replace('_', '/');
            int padding = (4 - base64.Length % 4) % 4;
            base64 = base64 + new string('=', padding);
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch (FormatException ex)
            {
                throw new CursorFormatException("Cursor is not valid base64url.", ex);
            }
            string decoded;
            try
            {
                decoded = Encoding.UTF8.GetString(bytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new CursorFormatException("Cursor is not valid UTF-8.", ex);
            }
            var parts = decoded.Split(Separator);
            if (parts.Length != 5) throw new CursorFormatException($"Cursor expects 5 fields, found {parts.Length}.");
            if (string.IsNullOrEmpty(parts[0])) throw new CursorFormatException("Schema version is missing.");
            if (string.IsNullOrEmpty(parts[1])) throw new CursorFormatException("Document key is missing.");
            if (string.IsNullOrEmpty(parts[2])) throw new CursorFormatException("Filter hash is missing.");
            int pageNumber;
            if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out pageNumber))
            {
                throw new CursorFormatException("Page number is not a valid integer.");
            }
            if (pageNumber < 0) throw new CursorFormatException("Page number must be non-negative.");
            return new Cursor(parts[0], parts[1], parts[2], parts[3] ?? string.Empty, pageNumber);
        }

        /// <summary>
        ///     Validate that the decoded cursor matches the live schema and
        ///     filter hash. Returns null on success; otherwise a rejection
        ///     reason safe to surface to the client.
        /// </summary>
        public static string? Validate(Cursor cursor, string expectedSchemaVersion, string expectedFilterHash, string expectedDocumentKey)
        {
            if (cursor.SchemaVersion != expectedSchemaVersion) return "stale_schema_version";
            if (cursor.DocumentKey != expectedDocumentKey) return "document_mismatch";
            if (cursor.FilterHash != expectedFilterHash) return "filter_mismatch";
            return null;
        }
    }

    /// <summary>
    ///     Thrown when a cursor string cannot be decoded. The handler should
    ///     surface this as a structured validation error, not as a server
    ///     failure.
    /// </summary>
    public sealed class CursorFormatException : Exception
    {
        public CursorFormatException(string message) : base(message) { }
        public CursorFormatException(string message, Exception inner) : base(message, inner) { }
    }
}
