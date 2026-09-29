using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;


namespace RegisterGeometry
{
    /// <summary>
    ///     Stable hash of a filter set. Used to bind cursors to the filter
    ///     hash so that a stale cursor can be rejected when the filter set
    ///     changes between page requests.
    /// </summary>
    public static class FilterHasher
    {
        /// <summary>
        ///     Compute a deterministic hex digest of <paramref name="parts"/>.
        ///     Parts are appended in the order they appear, separated by the
        ///     ASCII unit-separator character (0x1F) which is unlikely to
        ///     occur in user-supplied values.
        /// </summary>
        public static string Compute(IReadOnlyList<string> parts)
        {
            if (parts == null) throw new ArgumentNullException(nameof(parts));
            using var ms = new MemoryStream();
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) ms.WriteByte(0x1F);
                var bytes = Encoding.UTF8.GetBytes(parts[i] ?? string.Empty);
                ms.Write(bytes, 0, bytes.Length);
            }
            ms.Position = 0;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(ms.ToArray());
            var sb = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>
        ///     Compute a digest from a single canonical-form string.
        /// </summary>
        public static string Compute(string canonicalForm)
        {
            return Compute(new[] { canonicalForm ?? string.Empty });
        }

        /// <summary>
        ///     Compute a digest of a structured filter by joining key/value
        ///     pairs in lexicographic key order. Values are stringified using
        ///     invariant culture so the result is locale-independent.
        /// </summary>
        public static string Compute(IDictionary<string, string> keyValuePairs)
        {
            if (keyValuePairs == null) throw new ArgumentNullException(nameof(keyValuePairs));
            var sorted = new SortedDictionary<string, string>(keyValuePairs, StringComparer.Ordinal);
            var parts = new List<string>(sorted.Count);
            foreach (var kv in sorted)
            {
                parts.Add($"{kv.Key}={kv.Value ?? string.Empty}");
            }
            return Compute(parts);
        }

        /// <summary>
        ///     Canonical formatter for floating-point values. Uses the round-trip
        ///     format so the same number produces the same string regardless of
        ///     the runtime culture.
        /// </summary>
        public static string FormatDouble(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>
        ///     Canonical formatter for booleans.
        /// </summary>
        public static string FormatBool(bool value)
        {
            return value ? "true" : "false";
        }

        /// <summary>
        ///     Canonical formatter for nullables.
        /// </summary>
        public static string FormatNullable<T>(T? value) where T : struct
        {
            return value.HasValue ? FormatDouble(Convert.ToDouble(value.Value, CultureInfo.InvariantCulture)) : "";
        }
    }
}
