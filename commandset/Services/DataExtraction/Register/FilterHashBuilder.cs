using System;
using System.Collections.Generic;
using System.Globalization;
using RevitMCPCommandSet.Models.DataExtraction.Register;

namespace RevitMCPCommandSet.Services.DataExtraction.Register
{
    /// <summary>
    ///     Builds the canonical form of an applied filter set and computes a
    ///     deterministic filter hash. The hash is embedded in cursors so that
    ///     a cursor produced for one filter set can be rejected when called
    ///     against a different one.
    /// </summary>
    /// <remarks>
    ///     The canonical form joins the keys of each filter dimension in
    ///     lexicographic order, using the round-trip format for floating
    ///     point values and lowercase booleans, separated by the ASCII
    ///     unit-separator character (0x1F). The format mirrors the helper
    ///     <c>FilterHasher</c> in the pure geometry library so the two
    ///     implementations agree on the wire format.
    /// </remarks>
    public static class FilterHashBuilder
    {
        private const char UnitSeparator = '';

        /// <summary>
        ///     Compute the canonical SHA-256 hex digest of the applied
        ///     <see cref="RegisterExtractionOptions"/>.
        /// </summary>
        public static string ComputeHash(RegisterExtractionOptions options, string viewId, string phaseId)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var parts = new List<string>
            {
                "pageSize=" + (options.PageSize?.ToString(CultureInfo.InvariantCulture) ?? ""),
                "designOptionPolicy=" + (options.DesignOptionPolicy?.ToString() ?? ""),
                "includeLinkedModels=" + FormatBool(options.IncludeLinkedModels),
                "coordinateSystem=" + (options.CoordinateSystem?.ToString() ?? ""),
                "includeEvidence=" + FormatBool(options.IncludeEvidence),
            };
            AppendList(parts, "levelIds", options.LevelIds);
            AppendList(parts, "levelNames", options.LevelNames);
            parts.Append("viewId=" + (viewId ?? string.Empty));
            parts.Append("phaseId=" + (phaseId ?? string.Empty));
            if (options.Tolerances != null)
            {
                var t = options.Tolerances;
                parts.Append("tolerances.angularDegrees=" + FormatDouble(t.AngularDegrees));
                parts.Append("tolerances.intersectionMm=" + FormatDouble(t.IntersectionMm));
                parts.Append("tolerances.groupingMm=" + FormatDouble(t.GroupingMm));
                parts.Append("tolerances.supportSearchMm=" + FormatDouble(t.SupportSearchMm));
                parts.Append("tolerances.snapMm=" + FormatDouble(t.SnapMm));
            }
            AppendParameterMap(parts, options.ParameterMap);
            return RegisterGeometry.FilterHasher.Compute(parts);
        }

        /// <summary>
        ///     Compute the filter hash for the three option variants. The
        ///     shared and per-tool shapes are unified so the resulting digest
        ///     is comparable across handlers.
        /// </summary>
        public static string ComputeGridHash(GridRegisterExtractionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var baseHash = ComputeHash(options, options.ViewId, options.PhaseId);
            var extra = new List<string>
            {
                "originGridUniqueId=" + (options.OriginGridUniqueId ?? string.Empty),
                "originGridName=" + (options.OriginGridName ?? string.Empty),
            };
            AppendAxisAssignments(extra, options.AxisAssignments);
            return RegisterGeometry.FilterHasher.Compute(new List<string> { baseHash }.ConcatStrings(extra));
        }

        /// <summary>
        ///     Filter hash for the column/wall variant.
        /// </summary>
        public static string ComputeColumnWallHash(ColumnWallRegisterExtractionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var baseHash = ComputeHash(options, options.ViewId, options.PhaseId);
            var extra = new List<string>
            {
                "includeArchitecturalColumns=" + FormatBool(options.IncludeArchitecturalColumns),
                "mergeStackedWalls=" + FormatBool(options.MergeStackedWalls),
                "maxWallGroupSpanMm=" + (options.MaxWallGroupSpanMm?.ToString("R", CultureInfo.InvariantCulture) ?? ""),
            };
            AppendCorePrefixes(extra, options.CorePrefixes);
            return RegisterGeometry.FilterHasher.Compute(new List<string> { baseHash }.ConcatStrings(extra));
        }

        /// <summary>
        ///     Filter hash for the beam variant.
        /// </summary>
        public static string ComputeBeamHash(BeamRegisterExtractionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var baseHash = ComputeHash(options, options.ViewId, options.PhaseId);
            var extra = new List<string>
            {
                "minClearSpanConfidence=" + (options.MinClearSpanConfidence?.ToString("R", CultureInfo.InvariantCulture) ?? ""),
                "includeCantilevers=" + FormatBool(options.IncludeCantilevers),
            };
            AppendSupportPrecedence(extra, options.SupportCategoryPrecedence);
            AppendParameterMap(extra, options.BeamTypeAliases);
            return RegisterGeometry.FilterHasher.Compute(new List<string> { baseHash }.ConcatStrings(extra));
        }

        private static void AppendList(List<string> sink, string key, IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                sink.Add($"{key}=");
                return;
            }
            var copy = new List<string>(values);
            copy.Sort(StringComparer.Ordinal);
            sink.Add($"{key}=" + string.Join(",", copy));
        }

        private static void AppendParameterMap(
            List<string> sink,
            IReadOnlyDictionary<string, List<string>> map)
        {
            if (map == null || map.Count == 0)
            {
                sink.Add("parameterMap=");
                return;
            }
            var keys = new List<string>(map.Keys);
            keys.Sort(StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder();
            foreach (var key in keys)
            {
                sb.Append(key).Append('=');
                var aliases = map[key] ?? new List<string>();
                var aliasCopy = new List<string>(aliases);
                aliasCopy.Sort(StringComparer.Ordinal);
                sb.Append('[').Append(string.Join(",", aliasCopy)).Append(']').Append(';');
            }
            sink.Add("parameterMap=" + sb.ToString());
        }

        private static void AppendAxisAssignments(
            List<string> sink,
            IReadOnlyDictionary<string, AxisFamily> assignments)
        {
            if (assignments == null || assignments.Count == 0)
            {
                sink.Add("axisAssignments=");
                return;
            }
            var keys = new List<string>(assignments.Keys);
            keys.Sort(StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder();
            foreach (var key in keys)
            {
                sb.Append(key).Append('=').Append(assignments[key].ToString()).Append(';');
            }
            sink.Add("axisAssignments=" + sb.ToString());
        }

        private static void AppendSupportPrecedence(
            List<string> sink,
            IReadOnlyList<SupportCategory> precedence)
        {
            if (precedence == null || precedence.Count == 0)
            {
                sink.Add("supportCategoryPrecedence=");
                return;
            }
            var copy = new List<SupportCategory>(precedence);
            copy.Sort();
            sink.Add("supportCategoryPrecedence=" + string.Join(",", copy));
        }

        /// <summary>
        ///     Append the canonical form of the <c>corePrefixes</c> list. The
        ///     prefixes are sorted so two requests that name the same set in
        ///     different orders hash to the same digest.
        /// </summary>
        private static void AppendCorePrefixes(
            List<string> sink,
            IReadOnlyList<string> prefixes)
        {
            if (prefixes == null || prefixes.Count == 0)
            {
                sink.Add("corePrefixes=");
                return;
            }
            var copy = new List<string>(prefixes);
            copy.Sort(StringComparer.Ordinal);
            sink.Add("corePrefixes=" + string.Join(",", copy));
        }

        private static string FormatBool(bool? value)
            => value.HasValue ? FormatBool(value.Value) : "";

        private static string FormatBool(bool value) => value ? "true" : "false";

        private static string FormatDouble(double value)
            => value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Small extension that lets us append a sequence of strings to a
    ///     backing list in one expression. Centralised here so the
    ///     canonical-form code does not need LINQ allocations.
    /// </summary>
    internal static class FilterHashBuilderExtensions
    {
        public static List<string> ConcatStrings(this List<string> head, IReadOnlyList<string> tail)
        {
            var result = new List<string>(head.Count + tail.Count);
            result.AddRange(head);
            result.AddRange(tail);
            return result;
        }
    }
}