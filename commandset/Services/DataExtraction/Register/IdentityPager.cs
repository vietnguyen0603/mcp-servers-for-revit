using System;
using System.Collections.Generic;

namespace RevitMCPCommandSet.Services.DataExtraction.Register
{
    /// <summary>
    ///     Generic identity key extracted from a record. The pager sorts
    ///     records by this key in lexicographic order so paging remains
    ///     stable across calls.
    /// </summary>
    /// <remarks>
    ///     Identity keys are composed of the document key, the element key
    ///     (which already encodes the link instance), and a per-record
    ///     discriminator so wall legs and beam segments of the same physical
    ///     element can be ordered deterministically.
    /// </remarks>
    public readonly struct IdentityKey : IComparable<IdentityKey>, IEquatable<IdentityKey>
    {
        public IdentityKey(string documentKey, string elementKey, string discriminator)
        {
            DocumentKey = documentKey ?? throw new ArgumentNullException(nameof(documentKey));
            ElementKey = elementKey ?? throw new ArgumentNullException(nameof(elementKey));
            Discriminator = discriminator ?? string.Empty;
        }

        public string DocumentKey { get; }

        public string ElementKey { get; }

        public string Discriminator { get; }

        public int CompareTo(IdentityKey other)
        {
            int c = string.CompareOrdinal(DocumentKey, other.DocumentKey);
            if (c != 0) return c;
            c = string.CompareOrdinal(ElementKey, other.ElementKey);
            if (c != 0) return c;
            return string.CompareOrdinal(Discriminator, other.Discriminator);
        }

        public bool Equals(IdentityKey other)
            => DocumentKey == other.DocumentKey
               && ElementKey == other.ElementKey
               && Discriminator == other.Discriminator;

        public override bool Equals(object obj) => obj is IdentityKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (DocumentKey?.GetHashCode() ?? 0);
                hash = hash * 31 + (ElementKey?.GetHashCode() ?? 0);
                hash = hash * 31 + (Discriminator?.GetHashCode() ?? 0);
                return hash;
            }
        }

        public override string ToString() => $"{DocumentKey}:{ElementKey}:{Discriminator}";
    }

    /// <summary>
    ///     Stable pager used by every register handler. The pager sorts the
    ///     records by <see cref="IdentityKey"/>, slices a page of the
    ///     configured size starting from the cursor's last record key, and
    ///     exposes the next-cursor key.
    /// </summary>
    public sealed class IdentityPager
    {
        private readonly int _pageSize;

        public IdentityPager(int pageSize)
        {
            if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
            _pageSize = pageSize;
        }

        public int PageSize => _pageSize;

        /// <summary>
        ///     Sort <paramref name="keys"/> in lexicographic order. The input
        ///     is not modified; a new array is returned.
        /// </summary>
        public static IdentityKey[] Sort(IReadOnlyList<IdentityKey> keys)
        {
            if (keys == null) throw new ArgumentNullException(nameof(keys));
            var copy = new IdentityKey[keys.Count];
            for (int i = 0; i < keys.Count; i++) copy[i] = keys[i];
            Array.Sort(copy);
            return copy;
        }

        /// <summary>
        ///     Select the page starting after <paramref name="afterKey"/>. When
        ///     <paramref name="afterKey"/> is null the page starts at index 0.
        ///     The returned page contains at most <see cref="PageSize"/> items.
        /// </summary>
        public PageSlice Select(IdentityKey[] sortedKeys, IdentityKey? afterKey)
        {
            if (sortedKeys == null) throw new ArgumentNullException(nameof(sortedKeys));

            int startIndex = 0;
            if (afterKey.HasValue)
            {
                startIndex = Array.BinarySearch(sortedKeys, afterKey.Value);
                if (startIndex < 0)
                {
                    // Cursor pointed to a key that no longer exists; resume at
                    // the next-larger key so the client still makes progress.
                    startIndex = ~startIndex;
                }
                else
                {
                    // Skip past the cursor key itself.
                    startIndex += 1;
                }
            }

            if (startIndex >= sortedKeys.Length)
            {
                return new PageSlice(Array.Empty<IdentityKey>(), sortedKeys.Length, hasMore: false);
            }

            int endIndex = Math.Min(startIndex + _pageSize, sortedKeys.Length);
            int length = endIndex - startIndex;
            var page = new IdentityKey[length];
            Array.Copy(sortedKeys, startIndex, page, 0, length);
            bool hasMore = endIndex < sortedKeys.Length;
            return new PageSlice(page, sortedKeys.Length, hasMore);
        }
    }

    /// <summary>
    ///     One page of identity keys. <see cref="TotalCount"/> is the total
    ///     number of records discovered; <see cref="HasMore"/> indicates
    ///     whether <see cref="Keys"/> contains the final page.
    /// </summary>
    public readonly struct PageSlice
    {
        public PageSlice(IdentityKey[] keys, int totalCount, bool hasMore)
        {
            Keys = keys ?? Array.Empty<IdentityKey>();
            TotalCount = totalCount;
            HasMore = hasMore;
        }

        public IdentityKey[] Keys { get; }

        public int TotalCount { get; }

        public bool HasMore { get; }
    }
}
