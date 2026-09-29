using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Utils;
using ElementIdAlias = RevitMCPCommandSet.Utils.ElementIdExtensions;

namespace RevitMCPCommandSet.Services.DataExtraction.Register
{
    /// <summary>
    ///     Phase predicate. Resolves the phase id (either explicit, view-driven,
    ///     or document default) and returns a function that determines whether
    ///     a given element was created or demolished in that phase.
    /// </summary>
    public sealed class PhaseFilter
    {
        /// <summary>
        ///     Phase resolution status for the current request.
        /// </summary>
        public readonly struct Resolution
        {
            public Resolution(Phase createdInPhase, Phase demolishedInPhase, bool hasPhases, string phaseId)
            {
                CreatedInPhase = createdInPhase;
                DemolishedInPhase = demolishedInPhase;
                HasPhases = hasPhases;
                PhaseId = phaseId;
            }

            public Phase CreatedInPhase { get; }
            public Phase DemolishedInPhase { get; }
            public bool HasPhases { get; }
            public string PhaseId { get; }
        }

        private readonly Resolution _resolution;

        private PhaseFilter(Resolution resolution)
        {
            _resolution = resolution;
        }

        public Resolution State => _resolution;

        /// <summary>
        ///     True when the document has no phases at all (Revit returns an
        ///     empty phase enumeration). In that case all elements are kept
        ///     regardless of phase id.
        /// </summary>
        public bool HasNoPhases => !_resolution.HasPhases;

        /// <summary>
        ///     Resolve the phase filter from the request.
        /// </summary>
        /// <param name="document">Document to inspect.</param>
        /// <param name="view">
        ///     Optional view. When the explicit phase id is null and a view is
        ///     supplied, the view's active phase is used.
        /// </param>
        /// <param name="phaseId">
        ///     Explicit phase id from the request. Takes precedence over the
        ///     view phase.
        /// </param>
        /// <param name="warnings">Warning sink for unresolved ids.</param>
        public static PhaseFilter Resolve(
            Document document,
            View view,
            string phaseId,
            List<Models.DataExtraction.Register.WarningEntry> warnings = null)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            Phase created = null;
            Phase demolished = null;
            string resolvedId = null;
            bool hasPhases = false;

            // Phase enumeration. If there are no phases the document is
            // pre-phase; we keep every element.
            var phases = new FilteredElementCollector(document).OfClass(typeof(Phase)).Cast<Phase>().ToList();
            hasPhases = phases.Count > 0;
            if (!hasPhases)
            {
                return new PhaseFilter(new Resolution(null, null, false, null));
            }

            Phase target = null;
            if (!string.IsNullOrEmpty(phaseId))
            {
                target = FindPhaseById(document, phaseId);
                if (target == null && warnings != null)
                {
                    warnings.Add(RegisterWarningFactory.PhaseNotFound(phaseId));
                }
            }
            if (target == null && view != null)
            {
                try
                {
                    target = view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() is ElementId id
                        ? phases.FirstOrDefault(p => p.Id == id)
                        : null;
                }
                catch (InvalidOperationException)
                {
                    target = null;
                }
            }
            if (target == null)
            {
                // Document default = last phase in the list (Revit convention).
                target = phases[phases.Count - 1];
            }

            created = target;
            // Find the next phase after the target, when one exists.
            var ordered = PhaseOrdering(document);
            var idx = ordered.IndexOf(target);
            demolished = idx >= 0 && idx + 1 < ordered.Count ? ordered[idx + 1] : null;

            resolvedId = ElementIdAlias.GetValue(target.Id).ToString();
            return new PhaseFilter(new Resolution(created, demolished, true, resolvedId));
        }

        /// <summary>
        ///     Predicate that determines whether <paramref name="element"/>
        ///     survives the resolved phase range. Elements that did not yet
        ///     exist in the target phase or were already demolished are
        ///     rejected; everything else passes.
        /// </summary>
        public bool Accepts(Element element)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            if (!_resolution.HasPhases) return true;

            var createdPhase = element.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId();
            if (createdPhase != null && _resolution.CreatedInPhase != null)
            {
                if (!PhaseIndexGreaterOrEqual(element.Document, createdPhase, _resolution.CreatedInPhase.Id))
                {
                    return false;
                }
            }
            var demolishedPhase = element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.AsElementId();
            if (demolishedPhase != null && _resolution.DemolishedInPhase != null)
            {
                if (PhaseIndexGreaterOrEqual(element.Document, demolishedPhase, _resolution.DemolishedInPhase.Id))
                {
                    return false;
                }
            }
            return true;
        }

        private static Phase FindPhaseById(Document document, string phaseId)
        {
            if (long.TryParse(phaseId, out var numeric))
            {
                var id = new ElementId(numeric);
                return new FilteredElementCollector(document).OfClass(typeof(Phase))
                    .Cast<Phase>().FirstOrDefault(p => p.Id == id);
            }
            return new FilteredElementCollector(document).OfClass(typeof(Phase))
                .Cast<Phase>().FirstOrDefault(p => p.UniqueId == phaseId);
        }

        private static List<Phase> PhaseOrdering(Document document)
        {
            var phases = new FilteredElementCollector(document).OfClass(typeof(Phase)).Cast<Phase>().ToList();
            phases.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return phases;
        }

        private static bool PhaseIndexGreaterOrEqual(Document document, ElementId a, ElementId b)
        {
            var ordered = PhaseOrdering(document);
            int ia = ordered.FindIndex(p => p.Id == a);
            int ib = ordered.FindIndex(p => p.Id == b);
            return ia >= 0 && ib >= 0 && ia >= ib;
        }
    }
}