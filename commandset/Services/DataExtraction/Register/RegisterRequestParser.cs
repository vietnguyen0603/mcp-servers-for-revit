using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using RegisterGeometry;
using RevitMCPCommandSet.Models.DataExtraction.Register;
using AxisFamily = RevitMCPCommandSet.Models.DataExtraction.Register.AxisFamily;
using SupportCategory = RevitMCPCommandSet.Models.DataExtraction.Register.SupportCategory;

namespace RevitMCPCommandSet.Services.DataExtraction.Register
{
    /// <summary>
    ///     Parses the JSON-RPC <c>params</c> token into the strongly-typed
    ///     options classes used by every register handler. The defaults and
    ///     caps match the Zod schema in
    ///     <c>server/src/utils/registerSchemas.ts</c> so the contract cannot
    ///     drift between the two halves of the stack.
    /// </summary>
    /// <remarks>
    ///     The parser is intentionally tolerant: missing or malformed values
    ///     fall back to the documented default rather than throwing. That
    ///     mirrors the Zod behaviour of falling back to <c>.default()</c>.
    ///     Structural contradictions (e.g. <c>designOptionPolicy "active"</c>
    ///     without <c>viewId</c>) still throw so handlers do not silently
    ///     misbehave.
    /// </remarks>
    public static class RegisterRequestParser
    {
        // Defaults pinned to the Zod schema. Keep in sync with
        // server/src/utils/registerSchemas.ts.
        public const int DefaultPageSize = 100;
        public const int MaxPageSize = 500;
        public const int MaxLevelListSize = 256;
        public const int MaxParameterMapKeys = 32;
        public const int MaxParameterMapAliasesPerKey = 20;
        public const int MaxStringLength = 128;
        public const int MaxParameterKeyLength = 64;
        public const int MaxParameterAliasLength = 64;

        public const double DefaultAngularDegrees = 0.5;
        public const double DefaultIntersectionMm = 5;
        public const double DefaultGroupingMm = 10;
        public const double DefaultSupportSearchMm = 500;
        public const double DefaultSnapMm = 1;

        public const double MaxAngularDegrees = 90;
        public const double MaxToleranceMm = 1000;
        public const double MaxSupportSearchMm = 10000;

        /// <summary>
        ///     Raised when the request cannot be honoured. The message is
        ///     safe to surface to the client.
        /// </summary>
        public sealed class RegisterParseException : Exception
        {
            public RegisterParseException(string message) : base(message) { }
        }

        /// <summary>
        ///     Parse the request options from a JSON-RPC params token into a
        ///     <see cref="RegisterExtractionOptions"/>. The supplied token may
        ///     be null (an empty request); in that case defaults are applied.
        /// </summary>
        public static RegisterExtractionOptions ParseShared(JToken parameters)
        {
            return ParseSharedInternal(parameters, out _);
        }

        /// <summary>
        ///     Parse and also surface the list of warnings produced while
        ///     sanitising values (e.g. out-of-range tolerances silently
        ///     clamped). Handlers append them to the response envelope.
        /// </summary>
        public static RegisterExtractionOptions ParseShared(JToken parameters, out List<WarningEntry> warnings)
        {
            return ParseSharedInternal(parameters, out warnings);
        }

        /// <summary>
        ///     Parse the grid-specific options. The base options are merged
        ///     with the grid-only fields and re-validated.
        /// </summary>
        public static GridRegisterExtractionOptions ParseGrid(JToken parameters, out List<WarningEntry> warnings)
        {
            warnings = new List<WarningEntry>();
            var baseOptions = ParseSharedInternal(parameters, out var baseWarnings);
            warnings.AddRange(baseWarnings);
            var grid = new GridRegisterExtractionOptions
            {
                LevelIds = baseOptions.LevelIds,
                LevelNames = baseOptions.LevelNames,
                ViewId = baseOptions.ViewId,
                PhaseId = baseOptions.PhaseId,
                DesignOptionPolicy = baseOptions.DesignOptionPolicy,
                IncludeLinkedModels = baseOptions.IncludeLinkedModels,
                CoordinateSystem = baseOptions.CoordinateSystem,
                PageSize = baseOptions.PageSize,
                Cursor = baseOptions.Cursor,
                IncludeEvidence = baseOptions.IncludeEvidence,
                Tolerances = baseOptions.Tolerances,
                ParameterMap = baseOptions.ParameterMap,
            };

            if (parameters is JObject obj)
            {
                grid.OriginGridUniqueId = ReadOptionalString(obj, "originGridUniqueId", MaxStringLength);
                grid.OriginGridName = ReadOptionalString(obj, "originGridName", MaxStringLength);
                grid.AxisAssignments = ReadAxisAssignments(obj["axisAssignments"], warnings);
            }
            ValidateShared(grid, warnings);
            return grid;
        }

        /// <summary>
        ///     Parse the column/wall options.
        /// </summary>
        public static ColumnWallRegisterExtractionOptions ParseColumnWall(JToken parameters, out List<WarningEntry> warnings)
        {
            warnings = new List<WarningEntry>();
            var baseOptions = ParseSharedInternal(parameters, out var baseWarnings);
            warnings.AddRange(baseWarnings);
            var cw = new ColumnWallRegisterExtractionOptions
            {
                LevelIds = baseOptions.LevelIds,
                LevelNames = baseOptions.LevelNames,
                ViewId = baseOptions.ViewId,
                PhaseId = baseOptions.PhaseId,
                DesignOptionPolicy = baseOptions.DesignOptionPolicy,
                IncludeLinkedModels = baseOptions.IncludeLinkedModels,
                CoordinateSystem = baseOptions.CoordinateSystem,
                PageSize = baseOptions.PageSize,
                Cursor = baseOptions.Cursor,
                IncludeEvidence = baseOptions.IncludeEvidence,
                Tolerances = baseOptions.Tolerances,
                ParameterMap = baseOptions.ParameterMap,
            };

            if (parameters is JObject obj)
            {
                cw.IncludeArchitecturalColumns = ReadOptionalBool(obj, "includeArchitecturalColumns");
                cw.MergeStackedWalls = ReadOptionalBool(obj, "mergeStackedWalls");
                cw.MaxWallGroupSpanMm = ReadOptionalPositiveDouble(obj, "maxWallGroupSpanMm", warnings);
                cw.CorePrefixes = ReadCorePrefixes(obj["corePrefixes"], warnings);
            }

            ValidateShared(cw, warnings);
            return cw;
        }

        /// <summary>
        ///     Parse the beam-specific options.
        /// </summary>
        public static BeamRegisterExtractionOptions ParseBeam(JToken parameters, out List<WarningEntry> warnings)
        {
            warnings = new List<WarningEntry>();
            var baseOptions = ParseSharedInternal(parameters, out var baseWarnings);
            warnings.AddRange(baseWarnings);
            var beam = new BeamRegisterExtractionOptions
            {
                LevelIds = baseOptions.LevelIds,
                LevelNames = baseOptions.LevelNames,
                ViewId = baseOptions.ViewId,
                PhaseId = baseOptions.PhaseId,
                DesignOptionPolicy = baseOptions.DesignOptionPolicy,
                IncludeLinkedModels = baseOptions.IncludeLinkedModels,
                CoordinateSystem = baseOptions.CoordinateSystem,
                PageSize = baseOptions.PageSize,
                Cursor = baseOptions.Cursor,
                IncludeEvidence = baseOptions.IncludeEvidence,
                Tolerances = baseOptions.Tolerances,
                ParameterMap = baseOptions.ParameterMap,
            };

            if (parameters is JObject obj)
            {
                beam.SupportCategoryPrecedence = ReadSupportPrecedence(obj["supportCategoryPrecedence"], warnings);
                beam.MinClearSpanConfidence = ReadOptionalUnitInterval(obj, "minClearSpanConfidence", warnings);
                beam.IncludeCantilevers = ReadOptionalBool(obj, "includeCantilevers");
                beam.BeamTypeAliases = ReadBeamTypeAliases(obj["beamTypeAliases"], warnings);
            }
            ValidateShared(beam, warnings);
            return beam;
        }

        private static RegisterExtractionOptions ParseSharedInternal(JToken parameters, out List<WarningEntry> warnings)
        {
            warnings = new List<WarningEntry>();
            var options = new RegisterExtractionOptions();
            if (parameters is not JObject obj)
            {
                ApplyDefaults(options);
                ValidateShared(options, warnings);
                return options;
            }

            options.LevelIds = ReadStringList(obj["levelIds"], MaxLevelListSize, warnings, "levelIds");
            options.LevelNames = ReadStringList(obj["levelNames"], MaxLevelListSize, warnings, "levelNames");
            options.ViewId = ReadOptionalString(obj, "viewId", MaxStringLength);
            options.PhaseId = ReadOptionalString(obj, "phaseId", MaxStringLength);
            options.DesignOptionPolicy = ReadEnum<DesignOptionPolicy>(obj, "designOptionPolicy", warnings);
            options.IncludeLinkedModels = ReadOptionalBool(obj, "includeLinkedModels");
            options.CoordinateSystem = ReadEnum<CoordinateSystem>(obj, "coordinateSystem", warnings);
            options.PageSize = ReadPageSize(obj, warnings);
            options.Cursor = ReadOptionalString(obj, "cursor", CursorCodec.MaxWireLength);
            options.IncludeEvidence = ReadOptionalBool(obj, "includeEvidence");
            options.Tolerances = ReadTolerances(obj["tolerances"], warnings);
            options.ParameterMap = ReadParameterMap(obj["parameterMap"], warnings);

            ApplyDefaults(options);
            ValidateShared(options, warnings);
            return options;
        }

        private static void ApplyDefaults(RegisterExtractionOptions options)
        {
            if (!options.PageSize.HasValue) options.PageSize = DefaultPageSize;
            if (!options.DesignOptionPolicy.HasValue) options.DesignOptionPolicy = DesignOptionPolicy.Primary;
            if (!options.IncludeLinkedModels.HasValue) options.IncludeLinkedModels = false;
            if (!options.CoordinateSystem.HasValue) options.CoordinateSystem = CoordinateSystem.Project;
            if (!options.IncludeEvidence.HasValue) options.IncludeEvidence = true;
            options.Tolerances ??= new ToleranceSettingsDto
            {
                AngularDegrees = DefaultAngularDegrees,
                IntersectionMm = DefaultIntersectionMm,
                GroupingMm = DefaultGroupingMm,
                SupportSearchMm = DefaultSupportSearchMm,
                SnapMm = DefaultSnapMm,
            };
        }

        private static void ValidateShared(RegisterExtractionOptions options, List<WarningEntry> warnings)
        {
            // Clamp page size to the documented cap, surface a warning.
            if (options.PageSize is int requestedPageSize && requestedPageSize > MaxPageSize)
            {
                options.PageSize = MaxPageSize;
                warnings.Add(RegisterWarningFactory.Build(
                    WarningSeverity.Warning,
                    "page_size_clamped",
                    $"pageSize was {requestedPageSize}; clamped to {MaxPageSize}.",
                    detail: new Dictionary<string, object>
                    {
                        ["requested"] = requestedPageSize,
                        ["applied"] = MaxPageSize,
                    }));
            }

            // designOptionPolicy "active" requires viewId.
            if (options.DesignOptionPolicy == DesignOptionPolicy.Active && string.IsNullOrEmpty(options.ViewId))
            {
                throw new RegisterParseException(
                    "designOptionPolicy \"active\" requires viewId because the active design option is view-scoped.");
            }
        }

        private static string ReadOptionalString(JObject obj, string key, int maxLength)
        {
            var token = obj[key];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.String) return null;
            var value = token.Value<string>();
            if (value == null) return null;
            if (value.Length > maxLength) value = value.Substring(0, maxLength);
            return value;
        }

        private static bool? ReadOptionalBool(JObject obj, string key)
        {
            var token = obj[key];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            return null;
        }

        private static int? ReadPageSize(JObject obj, List<WarningEntry> warnings)
        {
            var token = obj["pageSize"];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) return null;
            int value = Convert.ToInt32(token.Value<double>(), CultureInfo.InvariantCulture);
            if (value <= 0)
            {
                warnings.Add(RegisterWarningFactory.Build(
                    WarningSeverity.Warning,
                    "page_size_invalid",
                    $"pageSize must be positive; ignoring {value}."));
                return null;
            }
            return value;
        }

        private static T? ReadEnum<T>(JObject obj, string key, List<WarningEntry> warnings) where T : struct, Enum
        {
            var token = obj[key];
            if (token == null || token.Type == JTokenType.Null) return null;
            string raw = token.Type switch
            {
                JTokenType.String => token.Value<string>(),
                JTokenType.Integer => token.Value<int>().ToString(CultureInfo.InvariantCulture),
                _ => null,
            };
            if (raw == null) return null;
            if (Enum.TryParse<T>(raw, ignoreCase: true, out var parsed))
            {
                return parsed;
            }
            warnings.Add(RegisterWarningFactory.Build(
                WarningSeverity.Warning,
                "enum_invalid",
                $"Unrecognised {key} value '{raw}'; using default."));
            return null;
        }

        private static ToleranceSettingsDto ReadTolerances(JToken token, List<WarningEntry> warnings)
        {
            if (token is not JObject obj)
            {
                return new ToleranceSettingsDto
                {
                    AngularDegrees = DefaultAngularDegrees,
                    IntersectionMm = DefaultIntersectionMm,
                    GroupingMm = DefaultGroupingMm,
                    SupportSearchMm = DefaultSupportSearchMm,
                    SnapMm = DefaultSnapMm,
                };
            }
            return new ToleranceSettingsDto
            {
                AngularDegrees = ReadClampedPositive(obj, "angularDegrees", DefaultAngularDegrees, 1e-3, MaxAngularDegrees, warnings, "tolerances.angularDegrees"),
                IntersectionMm = ReadClampedPositive(obj, "intersectionMm", DefaultIntersectionMm, 1e-6, MaxToleranceMm, warnings, "tolerances.intersectionMm"),
                GroupingMm = ReadClampedPositive(obj, "groupingMm", DefaultGroupingMm, 1e-6, MaxToleranceMm, warnings, "tolerances.groupingMm"),
                SupportSearchMm = ReadClampedPositive(obj, "supportSearchMm", DefaultSupportSearchMm, 1e-6, MaxSupportSearchMm, warnings, "tolerances.supportSearchMm"),
                SnapMm = ReadClampedPositive(obj, "snapMm", DefaultSnapMm, 0.0, MaxToleranceMm, warnings, "tolerances.snapMm"),
            };
        }

        private static double ReadClampedPositive(
            JObject obj,
            string key,
            double defaultValue,
            double minValue,
            double maxValue,
            List<WarningEntry> warnings,
            string path)
        {
            var token = obj[key];
            if (token == null || token.Type == JTokenType.Null) return defaultValue;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) return defaultValue;
            double value = token.Value<double>();
            if (double.IsNaN(value) || double.IsInfinity(value) || value < minValue || value > maxValue)
            {
                warnings.Add(RegisterWarningFactory.Build(
                    WarningSeverity.Warning,
                    "tolerance_out_of_range",
                    $"{path} value {value} is out of [{minValue}, {maxValue}]; using default {defaultValue}.",
                    detail: new Dictionary<string, object>
                    {
                        ["field"] = path,
                        ["requested"] = value,
                        ["applied"] = defaultValue,
                    }));
                return defaultValue;
            }
            return value;
        }

        private static List<string> ReadStringList(
            JToken token,
            int maxCount,
            List<WarningEntry> warnings,
            string fieldName)
        {
            if (token == null || token.Type != JTokenType.Array) return new List<string>();
            var list = new List<string>();
            foreach (var item in (JArray)token)
            {
                if (item.Type != JTokenType.String) continue;
                var s = item.Value<string>();
                if (string.IsNullOrEmpty(s)) continue;
                if (s.Length > MaxStringLength) s = s.Substring(0, MaxStringLength);
                list.Add(s);
                if (list.Count >= maxCount)
                {
                    warnings.Add(RegisterWarningFactory.Build(
                        WarningSeverity.Warning,
                        "list_truncated",
                        $"{fieldName} was truncated to {maxCount} entries."));
                    break;
                }
            }
            return list;
        }

        private static Dictionary<string, List<string>> ReadParameterMap(JToken token, List<WarningEntry> warnings)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (token == null || token.Type != JTokenType.Object) return result;
            var obj = (JObject)token;
            foreach (var kv in obj)
            {
                if (kv.Key.Length == 0 || kv.Key.Length > MaxParameterKeyLength) continue;
                if (result.Count >= MaxParameterMapKeys)
                {
                    warnings.Add(RegisterWarningFactory.Build(
                        WarningSeverity.Warning,
                        "parameter_map_truncated",
                        $"parameterMap was truncated to {MaxParameterMapKeys} keys."));
                    break;
                }
                if (kv.Value is not JArray array) continue;
                var aliases = new List<string>();
                foreach (var item in array)
                {
                    if (item.Type != JTokenType.String) continue;
                    var s = item.Value<string>();
                    if (string.IsNullOrEmpty(s)) continue;
                    if (s.Length > MaxParameterAliasLength) s = s.Substring(0, MaxParameterAliasLength);
                    aliases.Add(s);
                    if (aliases.Count >= MaxParameterMapAliasesPerKey) break;
                }
                result[kv.Key] = aliases;
            }
            return result;
        }

        private const int MaxAxisAssignments = 128;
        private const int MaxAxisAssignmentGridNames = 256;
        private const int MaxAxisAssignmentUniqueIdLength = 128;

        private static Dictionary<string, AxisFamily> ReadAxisAssignments(JToken token, List<WarningEntry> warnings)
        {
            if (token == null || token.Type != JTokenType.Array) return null;
            var result = new Dictionary<string, AxisFamily>(StringComparer.Ordinal);
            var array = (JArray)token;
            if (array.Count > MaxAxisAssignments)
            {
                warnings.Add(RegisterWarningFactory.Build(
                    WarningSeverity.Warning,
                    "axis_assignments_truncated",
                    $"axisAssignments has {array.Count} entries; truncated to {MaxAxisAssignments}."));
            }
            int processed = 0;
            foreach (var item in array)
            {
                if (processed >= MaxAxisAssignments) break;
                processed++;
                if (item is not JObject entry) continue;
                var rawFamily = entry["axisFamily"]?.Value<string>();
                if (string.IsNullOrEmpty(rawFamily))
                {
                    warnings.Add(RegisterWarningFactory.Build(
                        WarningSeverity.Warning,
                        "axis_assignment_missing_family",
                        "axisAssignments entry is missing axisFamily; skipped."));
                    continue;
                }
                if (!Enum.TryParse<AxisFamily>(rawFamily, ignoreCase: true, out var family))
                {
                    warnings.Add(RegisterWarningFactory.Build(
                        WarningSeverity.Warning,
                        "axis_family_invalid",
                        $"axisFamily value '{rawFamily}' is not recognised; skipped.",
                        detail: new Dictionary<string, object> { ["axisFamily"] = rawFamily }));
                    continue;
                }

                var namesToken = entry["gridNames"];
                var uniqueId = ReadOptionalString(entry, "uniqueId", MaxAxisAssignmentUniqueIdLength);

                var seenInEntry = new HashSet<string>(StringComparer.Ordinal);
                if (namesToken is JArray names)
                {
                    if (names.Count > MaxAxisAssignmentGridNames)
                    {
                        warnings.Add(RegisterWarningFactory.Build(
                            WarningSeverity.Warning,
                            "axis_assignment_names_truncated",
                            $"axisAssignments entry has {names.Count} gridNames; truncated to {MaxAxisAssignmentGridNames}."));
                    }
                    int added = 0;
                    foreach (var nameToken in names)
                    {
                        if (added >= MaxAxisAssignmentGridNames) break;
                        if (nameToken.Type != JTokenType.String) continue;
                        var raw = nameToken.Value<string>();
                        if (string.IsNullOrEmpty(raw)) continue;
                        var name = raw.Length > MaxStringLength ? raw.Substring(0, MaxStringLength) : raw;
                        if (!seenInEntry.Add(name)) continue;
                        TryAssign(result, name, family, warnings);
                        added++;
                    }
                }
                else if (namesToken != null && namesToken.Type != JTokenType.Null)
                {
                    warnings.Add(RegisterWarningFactory.Build(
                        WarningSeverity.Warning,
                        "axis_assignment_names_invalid",
                        "axisAssignments entry's gridNames must be an array; skipped."));
                }

                if (!string.IsNullOrEmpty(uniqueId))
                {
                    if (!seenInEntry.Add(uniqueId))
                    {
                        // duplicate of a name above: already attempted
                    }
                    TryAssign(result, uniqueId, family, warnings);
                }
            }
            return result;
        }

        private static void TryAssign(
            Dictionary<string, AxisFamily> result,
            string key,
            AxisFamily family,
            List<WarningEntry> warnings)
        {
            if (result.TryGetValue(key, out var existing))
            {
                if (existing == family) return; // idempotent
                warnings.Add(RegisterWarningFactory.Build(
                    WarningSeverity.Warning,
                    "axis_assignment_conflict",
                    $"Grid '{key}' was assigned to both '{existing}' and '{family}'; keeping '{existing}'.",
                    detail: new Dictionary<string, object>
                    {
                        ["gridKey"] = key,
                        ["kept"] = existing.ToString(),
                        ["rejected"] = family.ToString(),
                    }));
                return;
            }
            result[key] = family;
        }

        private static List<SupportCategory> ReadSupportPrecedence(JToken token, List<WarningEntry> warnings)
        {
            if (token == null || token.Type != JTokenType.Array) return null;
            var list = new List<SupportCategory>();
            var seen = new HashSet<SupportCategory>();
            foreach (var item in (JArray)token)
            {
                if (item.Type != JTokenType.String) continue;
                var s = item.Value<string>();
                if (string.IsNullOrEmpty(s)) continue;
                if (!Enum.TryParse<SupportCategory>(s, ignoreCase: true, out var cat))
                {
                    warnings.Add(RegisterWarningFactory.Build(
                        WarningSeverity.Warning,
                        "support_category_invalid",
                        $"supportCategoryPrecedence value '{s}' is not recognised; skipping."));
                    continue;
                }
                if (!seen.Add(cat))
                {
                    warnings.Add(RegisterWarningFactory.Build(
                        WarningSeverity.Warning,
                        "support_category_duplicate",
                        $"supportCategoryPrecedence value '{s}' was repeated; keeping the first."));
                    continue;
                }
                list.Add(cat);
            }
            return list;
        }

        private static double? ReadOptionalPositiveDouble(JObject obj, string key, List<WarningEntry> warnings)
        {
            var token = obj[key];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) return null;
            double value = token.Value<double>();
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return null;
            return value;
        }

        private static Dictionary<string, List<string>> ReadBeamTypeAliases(
            JToken token,
            List<WarningEntry> warnings)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Object) return null;
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var obj = (JObject)token;
            foreach (var kv in obj)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                if (kv.Key.Length > MaxParameterKeyLength) continue;
                if (result.Count >= MaxParameterMapKeys)
                {
                    warnings.Add(RegisterWarningFactory.Build(
                        WarningSeverity.Warning,
                        "beam_type_aliases_truncated",
                        $"beamTypeAliases was truncated to {MaxParameterMapKeys} keys."));
                    break;
                }
                if (kv.Value is not JArray array) continue;
                var aliases = new List<string>();
                foreach (var item in array)
                {
                    if (item.Type != JTokenType.String) continue;
                    var s = item.Value<string>();
                    if (string.IsNullOrEmpty(s)) continue;
                    if (s.Length > MaxParameterAliasLength) s = s.Substring(0, MaxParameterAliasLength);
                    aliases.Add(s);
                    if (aliases.Count >= MaxParameterMapAliasesPerKey) break;
                }
                result[kv.Key] = aliases;
            }
            return result;
        }

        private static double? ReadOptionalUnitInterval(JObject obj, string key, List<WarningEntry> warnings)
        {
            var token = obj[key];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) return null;
            double value = token.Value<double>();
            if (double.IsNaN(value) || double.IsInfinity(value)) return null;
            if (value < 0 || value > 1)
            {
                warnings.Add(RegisterWarningFactory.Build(
                    WarningSeverity.Warning,
                    "unit_interval_out_of_range",
                    $"{key} value {value} is outside [0,1]; using raw value."));
            }
            return value;
        }

        // Hard cap mirrors server/src/utils/registerSchemas.ts
        // (z.array(z.string().min(1).max(32)).max(64)).
        private const int MaxCorePrefixCount = 64;
        private const int MaxCorePrefixLength = 32;

        /// <summary>
        ///     Read the optional <c>corePrefixes</c> list used by the
        ///     column/wall handler as grouping evidence. Empty/blank entries
        ///     are dropped silently; lists exceeding the cap are truncated
        ///     with a warning so the response stays self-describing.
        /// </summary>
        private static List<string> ReadCorePrefixes(JToken token, List<WarningEntry> warnings)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Array)
            {
                warnings.Add(RegisterWarningFactory.Build(
                    WarningSeverity.Warning,
                    "core_prefixes_invalid",
                    "corePrefixes must be an array of strings; ignored."));
                return null;
            }
            var list = new List<string>();
            int discarded = 0;
            foreach (var item in (JArray)token)
            {
                if (item.Type != JTokenType.String) { discarded++; continue; }
                var raw = item.Value<string>();
                if (string.IsNullOrWhiteSpace(raw)) { discarded++; continue; }
                var trimmed = raw.Trim();
                if (trimmed.Length > MaxCorePrefixLength)
                {
                    trimmed = trimmed.Substring(0, MaxCorePrefixLength);
                }
                list.Add(trimmed);
                if (list.Count >= MaxCorePrefixCount)
                {
                    warnings.Add(RegisterWarningFactory.Build(
                        WarningSeverity.Warning,
                        "core_prefixes_truncated",
                        $"corePrefixes was truncated to {MaxCorePrefixCount} entries.",
                        detail: new Dictionary<string, object>
                        {
                            ["requested"] = ((JArray)token).Count,
                            ["applied"] = MaxCorePrefixCount,
                        }));
                    break;
                }
            }
            if (discarded > 0)
            {
                warnings.Add(RegisterWarningFactory.Build(
                    WarningSeverity.Info,
                    "core_prefixes_dropped",
                    $"{discarded} corePrefixes entries were empty or non-string; dropped."));
            }
            return list.Count == 0 ? null : list;
        }
    }
}
