namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    ///     Resolves an element (usually a type) by name: an exact case-insensitive
    ///     match first, then a unique partial match. Ambiguous or unknown names
    ///     throw with up to 30 candidate names so the caller can retry.
    /// </summary>
    internal static class TypeNameResolver
    {
        private const int MaxCandidates = 30;

        public static T Resolve<T>(IEnumerable<T> candidates, string name, string what) where T : Element
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException($"{what} name is empty.");

            var wanted = name.Trim();
            var list = candidates.Where(c => c != null).OrderBy(c => c.Id.GetValue()).ToList();

            var exact = list.FirstOrDefault(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;

            var partial = list.Where(c => c.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (partial.Count == 1)
                return partial[0];
            if (partial.Count > 1)
                throw new ArgumentException(
                    $"{what} '{wanted}' is ambiguous ({partial.Count} matches); pass the id or an exact name. Candidates: " +
                    Describe(partial));

            throw new ArgumentException($"{what} '{wanted}' not found. Available: " + Describe(list));
        }

        private static string Describe<T>(IReadOnlyCollection<T> elements) where T : Element
        {
            var text = string.Join("; ", elements.Take(MaxCandidates).Select(e => $"'{e.Name}' (id {e.Id.GetValue()})"));
            if (elements.Count > MaxCandidates)
                text += $"; ... {elements.Count - MaxCandidates} more";
            return text.Length == 0 ? "(none)." : text + ".";
        }
    }
}
