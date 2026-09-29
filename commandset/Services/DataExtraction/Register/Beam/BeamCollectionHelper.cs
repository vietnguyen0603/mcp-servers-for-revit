using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Models.DataExtraction.Register;
using RevitMCPCommandSet.Utils;
using ElementIdAlias = RevitMCPCommandSet.Utils.ElementIdExtensions;

namespace RevitMCPCommandSet.Services.DataExtraction.Register.Beam
{
    /// <summary>
    ///     Discovers structural framing instances and prepares the lightweight
    ///     identity records that survive pagination. Geometry probing is
    ///     deferred until the page is sliced so a large project only pays the
    ///     Solid intersection cost for the rows that actually ship.
    /// </summary>
    public sealed class BeamCollectionHelper
    {
        /// <summary>
        ///     Lightweight pass used during collection. Only fields safe to
        ///     read without geometry probes are populated; the
        ///     <see cref="BeamRecordBuilder"/> fills the rest later.
        /// </summary>
        public readonly struct IdentityEntry
        {
            public IdentityEntry(
                long elementId,
                string uniqueId,
                string documentKey,
                string linkInstanceUniqueId,
                LinkedDocumentContext linkContext,
                FamilyInstance instance,
                Document document)
            {
                ElementId = elementId;
                UniqueId = uniqueId;
                DocumentKey = documentKey;
                LinkInstanceUniqueId = linkInstanceUniqueId;
                LinkContext = linkContext;
                Instance = instance;
                Document = document;
            }

            public long ElementId { get; }
            public string UniqueId { get; }
            public string DocumentKey { get; }
            public string LinkInstanceUniqueId { get; }
            public LinkedDocumentContext LinkContext { get; }
            public FamilyInstance Instance { get; }
            public Document Document { get; }
        }

        /// <summary>
        ///     Walk the host document and any requested linked documents,
        ///     collecting <see cref="OST_StructuralFraming"/> instances. The
        ///     collector scope is document-wide; view-scoping is applied at
        ///     element-acceptance time so callers can mix options without
        ///     invalidating the collector.
        /// </summary>
        public List<IdentityEntry> Collect(
            Document hostDocument,
            HostScopeResolver scope,
            bool includeLinkedModels,
            LevelScopeResolver levels,
            PhaseFilter phaseFilter,
            DesignOptionFilter designFilter)
        {
            if (hostDocument == null) throw new ArgumentNullException(nameof(hostDocument));
            if (scope == null) throw new ArgumentNullException(nameof(scope));

            var entries = new List<IdentityEntry>(256);
            CollectFromDocument(
                hostDocument,
                scope,
                linkContext: null,
                levels,
                phaseFilter,
                designFilter,
                entries);

            if (includeLinkedModels)
            {
                var links = scope.ResolveLinkedContexts(true);
                foreach (var link in links)
                {
                    try
                    {
                        CollectFromDocument(
                            link.LinkDocument,
                            scope,
                            linkContext: link,
                            levels,
                            phaseFilter,
                            designFilter,
                            entries);
                    }
                    catch (InvalidOperationException)
                    {
                        // A link failed to load; skip it silently to keep
                        // the rest of the extraction running.
                    }
                }
            }
            return entries;
        }

        private static void CollectFromDocument(
            Document document,
            HostScopeResolver scope,
            LinkedDocumentContext linkContext,
            LevelScopeResolver levels,
            PhaseFilter phaseFilter,
            DesignOptionFilter designFilter,
            List<IdentityEntry> sink)
        {
            if (document == null) return;
            string documentKey = linkContext?.LinkedDocumentKey ?? scope.HostDocumentKey;
            string linkInstanceUniqueId = linkContext?.LinkInstanceUniqueId;

            // Structural framing spans both family instances and standalone
            // (non-host) structural framing elements; restrict to family
            // instances because beams are always parametric.
            var collector = linkContext == null
                ? new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_StructuralFraming)
                : new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_StructuralFraming);
            foreach (var elem in collector)
            {
                if (elem is not FamilyInstance fi) continue;
                if (phaseFilter != null && !phaseFilter.Accepts(fi)) continue;
                if (designFilter != null && !designFilter.Accepts(fi)) continue;

                // Level scope: when a level list was supplied, restrict
                // to instances whose LevelId matches. Beams hosted by a
                // roof/slab are kept when the request did not pin any
                // level.
                if (levels != null && levels.LevelCount > 0)
                {
                    long levelElementId = ElementIdAlias.GetValue(fi.LevelId);
                    bool inScope = false;
                    foreach (var level in levels.AllLevels)
                    {
                        if (ElementIdAlias.GetValue(level.Id) == levelElementId)
                        {
                            inScope = true;
                            break;
                        }
                    }
                    if (!inScope) continue;
                }

                long elementIdValue = ElementIdAlias.GetValue(fi.Id);
                sink.Add(new IdentityEntry(
                    elementIdValue,
                    fi.UniqueId,
                    documentKey,
                    linkInstanceUniqueId,
                    linkContext,
                    fi,
                    document));
            }
        }
    }
}