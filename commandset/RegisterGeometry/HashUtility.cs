using System;

namespace RegisterGeometry
{
    /// <summary>
    ///     Stable multi-field hash combine. <see cref="System.HashCode"/> is not
    ///     available on netstandard2.0 so this helper provides a tiny,
    ///     allocation-free substitute. The algorithm follows the well-known
    ///     "(seed * 397) ^ field" pattern used throughout the .NET reference
    ///     sources and is good enough for hash-table buckets and cursor
    ///     integrity checks.
    /// </summary>
    internal static class HashUtility
    {
        private const int Seed = unchecked((int)0x811C9DC5); // FNV offset basis, lower 32 bits
        private const int Multiplier = unchecked((int)0x01000193); // FNV prime, lower 32 bits

        public static int Combine(int h1, int h2)
        {
            unchecked
            {
                int h = Seed;
                h = (int)((h ^ (h1 & 0xFFFFFFFFL)) * (long)Multiplier);
                h = (int)((h ^ (h2 & 0xFFFFFFFFL)) * (long)Multiplier);
                return h;
            }
        }

        public static int Combine(int h1, int h2, int h3)
        {
            unchecked
            {
                return Combine(Combine(h1, h2), h3);
            }
        }

        public static int Combine(int h1, int h2, int h3, int h4)
        {
            unchecked
            {
                return Combine(Combine(h1, h2), Combine(h3, h4));
            }
        }

        public static int Combine(int h1, int h2, int h3, int h4, int h5)
        {
            unchecked
            {
                return Combine(Combine(h1, h2, h3), Combine(h4, h5));
            }
        }

        public static int Of<T>(T value)
        {
            return value == null ? 0 : value.GetHashCode();
        }
    }
}
