using System;
using System.Collections.Generic;
using System.Linq;

namespace RegisterGeometry
{
    /// <summary>
    ///     In-memory grid registry that supports family-scoped lookups. The
    ///     registry is constructed once from a collection of <see cref="GridLine"/>s,
    ///     sorted deterministically by unique id within each family so the
    ///     resulting order is independent of insertion sequence.
    /// </summary>
    public sealed class GridRegistry
    {
        private readonly Dictionary<string, GridLine> _byUniqueId;
        private readonly Dictionary<AxisFamily, List<GridLine>> _byFamily;

        public GridRegistry(IEnumerable<GridLine> grids)
        {
            if (grids == null) throw new ArgumentNullException(nameof(grids));
            _byUniqueId = new Dictionary<string, GridLine>(StringComparer.Ordinal);
            _byFamily = new Dictionary<AxisFamily, List<GridLine>>();
            foreach (var grid in grids)
            {
                if (grid == null) continue;
                _byUniqueId[grid.UniqueId] = grid;
                if (!_byFamily.TryGetValue(grid.Family, out var list))
                {
                    list = new List<GridLine>();
                    _byFamily[grid.Family] = list;
                }
                list.Add(grid);
            }

            foreach (var list in _byFamily.Values)
            {
                list.Sort((a, b) => string.CompareOrdinal(a.UniqueId, b.UniqueId));
            }
        }

        public int Count => _byUniqueId.Count;

        public IReadOnlyDictionary<AxisFamily, List<GridLine>> ByFamily => _byFamily;

        public bool TryGet(string uniqueId, out GridLine? grid)
        {
            var ok = _byUniqueId.TryGetValue(uniqueId, out var found);
            grid = found;
            return ok;
        }

        /// <summary>
        ///     All grids belonging to <paramref name="family"/>. Order is
        ///     deterministic (sorted by unique id).
        /// </summary>
        public IReadOnlyList<GridLine> InFamily(AxisFamily family)
        {
            return _byFamily.TryGetValue(family, out var list) ? list : Array.Empty<GridLine>();
        }

        /// <summary>
        ///     All grids regardless of family, sorted by family then unique id.
        /// </summary>
        public IEnumerable<GridLine> All()
        {
            return _byFamily
                .OrderBy(kv => (int)kv.Key)
                .SelectMany(kv => kv.Value);
        }
    }
}
