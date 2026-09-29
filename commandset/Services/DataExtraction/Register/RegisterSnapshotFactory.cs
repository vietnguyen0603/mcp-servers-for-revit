using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Models.DataExtraction.Register;

namespace RevitMCPCommandSet.Services.DataExtraction.Register
{
    /// <summary>
    ///     Builds the <see cref="RegisterSnapshot"/> block embedded in every
    ///     response. The snapshot is what makes the response self-describing:
    ///     document title, document key hash, Revit product version,
    ///     timestamp, active coordinate frame, and the resolved phase and
    ///     design option ids.
    /// </summary>
    public static class RegisterSnapshotFactory
    {
        /// <summary>
        ///     Build a snapshot from the host document. The supplied
        ///     <paramref name="documentKey"/> must come from
        ///     <see cref="RegisterKeyProvider.ResolveHost"/> so the snapshot
        ///     matches the cursor's filter hash.
        /// </summary>
        public static RegisterSnapshot Build(
            Document document,
            string documentKey,
            string revitVersion,
            CoordinateSystem system,
            string projectLocationName,
            string phaseId,
            string designOptionId,
            DateTime extractedAtUtc)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (documentKey == null) throw new ArgumentNullException(nameof(documentKey));

            return new RegisterSnapshot
            {
                DocumentTitle = document.Title ?? string.Empty,
                DocumentPathHash = documentKey,
                RevitVersion = revitVersion ?? string.Empty,
                ExtractedAtUtc = extractedAtUtc.ToUniversalTime()
                    .ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                CoordinateSystem = system,
                LengthUnit = "mm",
                PhaseId = phaseId,
                DesignOptionId = designOptionId,
                ProjectLocationName = projectLocationName,
            };
        }

        /// <summary>
        ///     Build the <see cref="FilterSummary"/> echoed in the response.
        ///     The summary is constructed from the resolved options so the
        ///     client sees the canonical values that were actually applied.
        /// </summary>
        public static FilterSummary BuildFilterSummary(
            RegisterExtractionOptions options,
            string filterHash,
            string resolvedPhaseId,
            IReadOnlyList<string> resolvedLevelIds,
            IReadOnlyList<string> resolvedLevelNames,
            string viewId)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            return new FilterSummary
            {
                LevelIds = resolvedLevelIds != null ? new List<string>(resolvedLevelIds) : null,
                LevelNames = resolvedLevelNames != null ? new List<string>(resolvedLevelNames) : null,
                ViewId = viewId,
                PhaseId = resolvedPhaseId,
                DesignOptionPolicy = options.DesignOptionPolicy ?? DesignOptionPolicy.Primary,
                IncludeLinkedModels = options.IncludeLinkedModels ?? false,
                CoordinateSystem = options.CoordinateSystem ?? CoordinateSystem.Project,
                IncludeEvidence = options.IncludeEvidence ?? true,
                Tolerances = options.Tolerances ?? new ToleranceSettingsDto(),
                FilterHash = filterHash,
            };
        }
    }
}