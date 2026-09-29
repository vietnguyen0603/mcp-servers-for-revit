using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Utils;
using ElementIdAlias = RevitMCPCommandSet.Utils.ElementIdExtensions;

namespace RevitMCPCommandSet.Services.DataExtraction.Register
{
    /// <summary>
    ///     Resolves a list of level ids/names against the host document and
    ///     returns the matching <see cref="Level"/> elements. Names that
    ///     are missing or ambiguous are dropped with a warning.
    /// </summary>
    public sealed class LevelScopeResolver
    {
        private readonly Document _document;
        private readonly Dictionary<long, Level> _levelsById;
        private readonly Dictionary<string, List<Level>> _levelsByName;

        public LevelScopeResolver(Document document)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
            _levelsById = new Dictionary<long, Level>();
            _levelsByName = new Dictionary<string, List<Level>>(StringComparer.OrdinalIgnoreCase);
            foreach (var lvl in new FilteredElementCollector(document).OfClass(typeof(Level)))
            {
                if (lvl is not Level level) continue;
                long id = ElementIdAlias.GetValue(level.Id);
                _levelsById[id] = level;
                if (!_levelsByName.TryGetValue(level.Name, out var list))
                {
                    list = new List<Level>();
                    _levelsByName[level.Name] = list;
                }
                list.Add(level);
            }
        }

        /// <summary>
        ///     Number of unique levels discovered in the document.
        /// </summary>
        public int LevelCount => _levelsById.Count;

        /// <summary>
        ///     All levels discovered in the document. Useful when neither
        ///     level ids nor level names were requested and the handler wants
        ///     a flat list.
        /// </summary>
        public IReadOnlyCollection<Level> AllLevels => _levelsById.Values;

        /// <summary>
        ///     Resolve a level by id. Returns null when the id is unknown.
        /// </summary>
        public Level ResolveById(long id) => _levelsById.TryGetValue(id, out var level) ? level : null;

        /// <summary>
        ///     Resolve a level by id parsed from a string id.
        /// </summary>
        public Level ResolveById(string id, List<Models.DataExtraction.Register.WarningEntry> warnings = null)
        {
            if (long.TryParse(id, out var numeric))
            {
                return ResolveById(numeric);
            }
            warnings?.Add(RegisterWarningFactory.LevelIdNotFound(id));
            return null;
        }

        /// <summary>
        ///     Resolve a level by name. When multiple levels share the same
        ///     name the resolver returns null and appends an
        ///     "ambiguous_level_name" warning. Missing names append a
        ///     "level_name_not_found" warning.
        /// </summary>
        public Level ResolveByName(string name, List<Models.DataExtraction.Register.WarningEntry> warnings = null)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (!_levelsByName.TryGetValue(name, out var list) || list.Count == 0)
            {
                warnings?.Add(RegisterWarningFactory.LevelNameNotFound(name));
                return null;
            }
            if (list.Count > 1)
            {
                var matches = new List<string>();
                foreach (var lvl in list) matches.Add(ElementIdAlias.GetValue(lvl.Id).ToString());
                warnings?.Add(RegisterWarningFactory.AmbiguousLevelName(name, matches));
                return null;
            }
            return list[0];
        }

        /// <summary>
        ///     Resolve every requested id/name. Items that cannot be resolved
        ///     are dropped (and surfaced via the warnings list); the returned
        ///     list contains only the unique, successfully resolved levels.
        /// </summary>
        public List<Level> ResolveAll(
            IReadOnlyList<string> ids,
            IReadOnlyList<string> names,
            List<Models.DataExtraction.Register.WarningEntry> warnings)
        {
            var resolved = new List<Level>();
            var seen = new HashSet<long>();
            if (ids != null)
            {
                foreach (var raw in ids)
                {
                    var level = ResolveById(raw, warnings);
                    if (level == null) continue;
                    long id = ElementIdAlias.GetValue(level.Id);
                    if (seen.Add(id)) resolved.Add(level);
                }
            }
            if (names != null)
            {
                foreach (var name in names)
                {
                    var level = ResolveByName(name, warnings);
                    if (level == null) continue;
                    long id = ElementIdAlias.GetValue(level.Id);
                    if (seen.Add(id)) resolved.Add(level);
                }
            }
            return resolved;
        }
    }
}