using System;
using System.Collections.Generic;
using RegisterGeometry;

namespace RevitMCPCommandSet.Services.DataExtraction.Register.Beam
{
    /// <summary>
    ///     Planar grid-cell spatial index over axis-aligned bounds. The index
    ///     is built once from the candidate support elements and reused for
    ///     every beam-end lookup, so the extraction does not pay O(N) per
    ///     beam end. Cell size defaults to the tolerance's
    ///     <c>SupportSearchMm</c> so the worst case visits a constant number
    ///     of candidates per query.
    /// </summary>
    public sealed class BeamSpatialIndex
    {
        private readonly double _cellSize;
        private readonly Dictionary<long, List<IndexedCandidate>> _cells = new Dictionary<long, List<IndexedCandidate>>(1024);

        public BeamSpatialIndex(double cellSize)
        {
            if (double.IsNaN(cellSize) || double.IsInfinity(cellSize) || cellSize <= 0)
            {
                cellSize = 1.0;
            }
            _cellSize = cellSize;
        }

        public int CellCount => _cells.Count;

        /// <summary>
        ///     One indexed candidate. The integer id is whatever opaque handle
        ///     the caller chose (typically the element id) and is returned
        ///     unchanged by <see cref="Query"/>.
        /// </summary>
        public readonly struct IndexedCandidate
        {
            public IndexedCandidate(long id, Bounds bounds, object payload)
            {
                Id = id;
                Bounds = bounds;
                Payload = payload;
            }

            public long Id { get; }
            public Bounds Bounds { get; }
            public object Payload { get; }
        }

        public void Insert(long id, Bounds bounds, object payload)
        {
            foreach (var key in CellKeys(bounds))
            {
                if (!_cells.TryGetValue(key, out var bucket))
                {
                    bucket = new List<IndexedCandidate>(4);
                    _cells[key] = bucket;
                }
                bucket.Add(new IndexedCandidate(id, bounds, payload));
            }
        }

        /// <summary>
        ///     Return every candidate whose bounds intersect the inflated
        ///     query window. Duplicates are de-duplicated by id.
        /// </summary>
        public List<IndexedCandidate> Query(Bounds window)
        {
            var seen = new HashSet<long>();
            var result = new List<IndexedCandidate>(8);
            foreach (var key in CellKeys(window))
            {
                if (!_cells.TryGetValue(key, out var bucket)) continue;
                foreach (var item in bucket)
                {
                    if (!seen.Add(item.Id)) continue;
                    result.Add(item);
                }
            }
            return result;
        }

        private IEnumerable<long> CellKeys(Bounds bounds)
        {
            if (bounds.IsEmpty) yield break;
            int minX = (int)Math.Floor(bounds.MinX / _cellSize);
            int minY = (int)Math.Floor(bounds.MinY / _cellSize);
            int maxX = (int)Math.Floor(bounds.MaxX / _cellSize);
            int maxY = (int)Math.Floor(bounds.MaxY / _cellSize);
            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    long packed = ((long)x << 32) ^ (uint)y;
                    yield return packed;
                }
            }
        }
    }
}