using System;
using System.Collections.Generic;
using RevitMCPCommandSet.Models.DataExtraction.Register;

namespace RevitMCPCommandSet.Services.DataExtraction.Register
{
    /// <summary>
    ///     Builds <see cref="WarningEntry"/> objects with stable snake_case
    ///     codes. Centralised so every handler emits the same vocabulary and
    ///     tests can match on the codes without copy/paste.
    /// </summary>
    public static class RegisterWarningFactory
    {
        /// <summary>No active document; abort early.</summary>
        public static WarningEntry NoActiveDocument()
            => Build(WarningSeverity.Error, "no_active_document", "The active Revit document is unavailable.");

        /// <summary>Requested view id could not be resolved.</summary>
        public static WarningEntry ViewNotFound(string viewId)
            => Build(WarningSeverity.Error, "view_not_found", $"View id '{viewId}' could not be resolved.", elementId: null, documentKey: null,
                detail: new Dictionary<string, object> { ["viewId"] = viewId });

        /// <summary>Level name was ambiguous (matched multiple levels).</summary>
        public static WarningEntry AmbiguousLevelName(string levelName, IReadOnlyList<string> matches)
            => Build(WarningSeverity.Warning, "ambiguous_level_name",
                $"Level name '{levelName}' matched multiple levels; dropped.",
                detail: new Dictionary<string, object>
                {
                    ["levelName"] = levelName,
                    ["matchedIds"] = new List<object>(matches ?? Array.Empty<string>()),
                });

        /// <summary>Level name was not found.</summary>
        public static WarningEntry LevelNameNotFound(string levelName)
            => Build(WarningSeverity.Warning, "level_name_not_found",
                $"Level name '{levelName}' did not match any level.",
                detail: new Dictionary<string, object> { ["levelName"] = levelName });

        /// <summary>Level id was not found.</summary>
        public static WarningEntry LevelIdNotFound(string levelId)
            => Build(WarningSeverity.Warning, "level_id_not_found",
                $"Level id '{levelId}' did not match any level.",
                detail: new Dictionary<string, object> { ["levelId"] = levelId });

        /// <summary>Phase id was not found.</summary>
        public static WarningEntry PhaseNotFound(string phaseId)
            => Build(WarningSeverity.Warning, "phase_not_found",
                $"Phase id '{phaseId}' did not match any phase.",
                detail: new Dictionary<string, object> { ["phaseId"] = phaseId });

        /// <summary>Design option id was not found.</summary>
        public static WarningEntry DesignOptionNotFound(string designOptionId)
            => Build(WarningSeverity.Warning, "design_option_not_found",
                $"Design option id '{designOptionId}' did not match any design option.",
                detail: new Dictionary<string, object> { ["designOptionId"] = designOptionId });

        /// <summary>Configuration required viewId but it was absent.</summary>
        public static WarningEntry ViewRequired(string reason)
            => Build(WarningSeverity.Error, "view_required",
                $"Request requires a viewId: {reason}");

        /// <summary>Origin grid configured but could not be resolved.</summary>
        public static WarningEntry MissingOrigin(string uniqueId, string name)
            => Build(WarningSeverity.Error, "missing_origin",
                "The configured origin grid could not be located in the registry.",
                detail: new Dictionary<string, object>
                {
                    ["originGridUniqueId"] = uniqueId ?? string.Empty,
                    ["originGridName"] = name ?? string.Empty,
                });

        /// <summary>Origin grid name was ambiguous.</summary>
        public static WarningEntry AmbiguousOriginName(string name, IReadOnlyList<string> matches)
            => Build(WarningSeverity.Error, "ambiguous_origin_name",
                $"Origin grid name '{name}' matched multiple grids; expected a unique match.",
                detail: new Dictionary<string, object>
                {
                    ["originGridName"] = name,
                    ["matchedUniqueIds"] = new List<object>(matches ?? Array.Empty<string>()),
                });

        /// <summary>Curve kind not handled by the response shape.</summary>
        public static WarningEntry UnsupportedCurveKind(string elementId, string kind)
            => Build(WarningSeverity.Warning, "unsupported_curve_kind",
                $"Curve kind '{kind}' is not represented in the response shape.",
                elementId: elementId,
                detail: new Dictionary<string, object> { ["kind"] = kind });

        /// <summary>Solid extraction failed for the element.</summary>
        public static WarningEntry SolidExtractionFailed(string elementId, string reason)
            => Build(WarningSeverity.Warning, "solid_extraction_failed",
                $"Solid extraction failed: {reason}",
                elementId: elementId);

        /// <summary>Parameter map exceeded the documented cap.</summary>
        public static WarningEntry ParameterMapTooLarge(int keyCount)
            => Build(WarningSeverity.Error, "parameter_map_too_large",
                $"parameterMap has {keyCount} keys; maximum is 32.");

        /// <summary>Generic warning factory.</summary>
        public static WarningEntry Build(
            WarningSeverity severity,
            string code,
            string message,
            string elementId = null,
            string documentKey = null,
            Dictionary<string, object> detail = null)
        {
            return new WarningEntry
            {
                Severity = severity,
                Code = code ?? throw new ArgumentNullException(nameof(code)),
                Message = message ?? string.Empty,
                ElementId = elementId,
                DocumentKey = documentKey,
                Detail = detail,
            };
        }
    }
}