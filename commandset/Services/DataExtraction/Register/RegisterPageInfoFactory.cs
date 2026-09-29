using System.Collections.Generic;
using RevitMCPCommandSet.Models.DataExtraction.Register;
using RegisterGeometry;

namespace RevitMCPCommandSet.Services.DataExtraction.Register
{
    /// <summary>
    ///     Builds the <see cref="PageInfo"/> block emitted alongside each
    ///     page. The factory hides the cursor codec behind a small surface
    ///     so they cannot drift between handlers.
    /// </summary>
    public static class RegisterPageInfoFactory
    {
        /// <summary>
        ///     Build a final-page payload. <see cref="PageInfo.NextCursor"/>
        ///     is null and <see cref="PageInfo.HasMore"/> is false.
        /// </summary>
        public static PageInfo BuildFinal(int returned)
        {
            return new PageInfo
            {
                NextCursor = null,
                Returned = returned,
                HasMore = false,
                TotalEstimate = null,
            };
        }

        /// <summary>
        ///     Build a non-final page payload. The next cursor is encoded from
        ///     the last record key in the page so the next call can resume
        ///     exactly where this one stopped.
        /// </summary>
        public static PageInfo BuildNext(
            string lastRecordKey,
            string documentKey,
            string filterHash,
            int nextPageNumber,
            int returned,
            int totalEstimate)
        {
            var cursor = CursorValidator.BuildNext(documentKey, filterHash, lastRecordKey, nextPageNumber);
            return new PageInfo
            {
                NextCursor = CursorCodec.Encode(cursor),
                Returned = returned,
                HasMore = true,
                TotalEstimate = totalEstimate,
            };
        }

        /// <summary>
        ///     Build a <see cref="PageInfo"/> from a precomputed
        ///     <see cref="PageSlice"/>. The last key in
        ///     <see cref="PageSlice.Keys"/> becomes the next-cursor anchor.
        /// </summary>
        public static PageInfo FromSlice(
            PageSlice slice,
            string documentKey,
            string filterHash,
            int nextPageNumber,
            int totalEstimate)
        {
            if (slice.HasMore)
            {
                var last = slice.Keys[slice.Keys.Length - 1];
                var recordKey = last.ElementKey + "|" + last.Discriminator;
                return BuildNext(recordKey, documentKey, filterHash, nextPageNumber, slice.Keys.Length, totalEstimate);
            }
            return BuildFinal(slice.Keys.Length);
        }
    }
}