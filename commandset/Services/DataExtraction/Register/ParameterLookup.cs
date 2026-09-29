using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Services.DataExtraction.Register
{
    /// <summary>
    ///     Resolve a logical parameter name (e.g. "mark", "width") to a Revit
    ///     <see cref="Parameter"/> using built-in lookups first and the
    ///     supplied aliases as a fallback. The lookup records provenance so
    ///     the handler can report how each derived field was sourced.
    /// </summary>
    /// <remarks>
    ///     Lookup order per logical key:
    ///     <list type="number">
    ///         <item>built-in parameter whose enum name matches the logical key
    ///             (case-insensitive),</item>
    ///         <item>each alias in the order given, trying the matching built-in
    ///             enum first and then a project/family parameter by name.</item>
    ///     </list>
    ///     The lookup never mutates the Revit document and is safe to call
    ///     outside of a transaction.
    /// </remarks>
    public sealed class ParameterLookup
    {
        private readonly Element _element;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _aliases;
        private readonly Dictionary<string, BuiltInParameter> _builtInByName;

        /// <summary>
        ///     Build a lookup bound to <paramref name="element"/>.
        /// </summary>
        /// <param name="element">Element to inspect.</param>
        /// <param name="aliases">
        ///     Optional parameter alias map keyed by logical name. Values are
        ///     the ordered list of candidate names to fall back to when the
        ///     built-in lookup fails. The map shape mirrors the
        ///     <c>parameterMap</c> option on the wire format.
        /// </param>
        public ParameterLookup(
            Element element,
            IReadOnlyDictionary<string, IReadOnlyList<string>> aliases = null)
        {
            _element = element ?? throw new ArgumentNullException(nameof(element));
            _aliases = aliases;
            _builtInByName = BuildBuiltInCache();
        }

        /// <summary>
        ///     Resolve the parameter for the given logical key.
        /// </summary>
        public ParameterResolution TryResolve(string logicalKey)
        {
            if (string.IsNullOrEmpty(logicalKey))
            {
                return ParameterResolution.NotFound("empty_key");
            }

            // Step 1: built-in parameter directly mapped from the logical key.
            var builtIn = TryResolveBuiltIn(logicalKey);
            if (builtIn != null)
            {
                return new ParameterResolution(
                    builtIn, ParameterProvenance.BuiltIn, logicalKey);
            }

            // Step 2: walk the aliases for the logical key, if any.
            if (_aliases != null && _aliases.TryGetValue(logicalKey, out var list) && list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var alias = list[i];
                    if (string.IsNullOrEmpty(alias)) continue;

                    var builtInAlias = TryResolveBuiltIn(alias);
                    if (builtInAlias != null)
                    {
                        return new ParameterResolution(
                            builtInAlias, ParameterProvenance.Alias, alias, i);
                    }

                    var named = _element.LookupParameter(alias);
                    if (named != null)
                    {
                        return new ParameterResolution(
                            named, ParameterProvenance.Alias, alias, i);
                    }
                }
            }

            // Step 3: parameter named exactly the logical key (project/family).
            var direct = _element.LookupParameter(logicalKey);
            if (direct != null)
            {
                return new ParameterResolution(
                    direct, ParameterProvenance.Alias, logicalKey, -1);
            }

            return ParameterResolution.NotFound(logicalKey);
        }

        /// <summary>
        ///     Read the parameter as a string using
        ///     <see cref="Parameter.AsString"/>. Returns null when the
        ///     parameter is missing, when the storage type is not string, or
        ///     when the value is empty.
        /// </summary>
        public string ResolveString(string logicalKey)
        {
            var resolved = TryResolve(logicalKey);
            if (resolved.Parameter == null) return null;
            if (resolved.Parameter.StorageType != StorageType.String) return null;
            var value = resolved.Parameter.AsString();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        /// <summary>
        ///     Read the parameter as a double (millimetres after the caller
        ///     applies any unit conversion). Returns null when the parameter
        ///     is missing or not a numeric value.
        /// </summary>
        public double? ResolveDouble(string logicalKey)
        {
            var resolved = TryResolve(logicalKey);
            if (resolved.Parameter == null) return null;
            switch (resolved.Parameter.StorageType)
            {
                case StorageType.Double:
                    return resolved.Parameter.AsDouble();
                case StorageType.Integer:
                    return resolved.Parameter.AsInteger();
                default:
                    return null;
            }
        }

        /// <summary>
        ///     Read the parameter as an integer. Returns null when the
        ///     parameter is missing or not an integer.
        /// </summary>
        public int? ResolveInteger(string logicalKey)
        {
            var resolved = TryResolve(logicalKey);
            if (resolved.Parameter == null) return null;
            return resolved.Parameter.StorageType switch
            {
                StorageType.Integer => resolved.Parameter.AsInteger(),
                StorageType.Double => (int)resolved.Parameter.AsDouble(),
                _ => null,
            };
        }

        private Parameter TryResolveBuiltIn(string name)
        {
            if (_builtInByName.TryGetValue(name.Trim().ToUpperInvariant(), out var builtIn))
            {
                return _element.get_Parameter(builtIn);
            }
            return null;
        }

        /// <summary>
        ///     Build a lookup from the alias-shaped name to a built-in parameter
        ///     enum. The cache is per-instance because it depends on the
        ///     aliases list which is bounded (at most 32 keys x 20 aliases
        ///     each).
        /// </summary>
        private Dictionary<string, BuiltInParameter> BuildBuiltInCache()
        {
            var map = new Dictionary<string, BuiltInParameter>(StringComparer.Ordinal);
            // Always map the logical keys themselves.
            foreach (BuiltInParameter bip in Enum.GetValues(typeof(BuiltInParameter)))
            {
                if (bip == BuiltInParameter.INVALID) continue;
                var name = bip.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                map[name.ToUpperInvariant()] = bip;
            }
            return map;
        }
    }

    /// <summary>
    ///     Result of a parameter lookup. The provenance indicates how the
    ///     value was sourced so downstream code can attach a derivation
    ///     block to its DTO.
    /// </summary>
    public readonly struct ParameterResolution
    {
        public ParameterResolution(
            Parameter parameter,
            ParameterProvenance provenance,
            string sourceName,
            int aliasIndex = -1)
        {
            Parameter = parameter;
            Provenance = provenance;
            SourceName = sourceName ?? string.Empty;
            AliasIndex = aliasIndex;
        }

        public Parameter Parameter { get; }

        public ParameterProvenance Provenance { get; }

        /// <summary>
        ///     Name of the resolved parameter (or alias text).
        /// </summary>
        public string SourceName { get; }

        /// <summary>
        ///     Index into the alias list, or -1 when not from an alias.
        /// </summary>
        public int AliasIndex { get; }

        public bool IsFound => Parameter != null;

        public static ParameterResolution NotFound(string sourceName)
            => new ParameterResolution(null, ParameterProvenance.NotFound, sourceName);
    }

    /// <summary>
    ///     Provenance of a resolved parameter. Mirrors the DTO
    ///     <c>DerivationInfo.Method</c> vocabulary but kept distinct so the
    ///     adapters can serialise without an extra cast.
    /// </summary>
    public enum ParameterProvenance
    {
        BuiltIn = 0,
        Alias = 1,
        NotFound = 2,
    }

    /// <summary>
    ///     Stable snake_case method strings for serialising parameter
    ///     provenance. Centralised so the DTO and the adapters cannot drift.
    /// </summary>
    public static class ParameterProvenanceCodes
    {
        public const string BuiltIn = "built_in_parameter";
        public const string Alias = "parameter_alias";
        public const string NotFound = "parameter_not_found";

        public static string For(ParameterProvenance provenance) => provenance switch
        {
            ParameterProvenance.BuiltIn => BuiltIn,
            ParameterProvenance.Alias => Alias,
            _ => NotFound,
        };
    }
}
