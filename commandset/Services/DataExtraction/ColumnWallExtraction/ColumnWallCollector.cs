using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Models.DataExtraction.Register;
using RevitMCPCommandSet.Services.DataExtraction.Register;
using RevitMCPCommandSet.Utils;
using ElementIdAlias = RevitMCPCommandSet.Utils.ElementIdExtensions;
using RPhaseFilter = RevitMCPCommandSet.Services.DataExtraction.Register.PhaseFilter;
using RDesignOptionFilter = RevitMCPCommandSet.Services.DataExtraction.Register.DesignOptionFilter;

namespace RevitMCPCommandSet.Services.DataExtraction.ColumnWallExtraction
{
    /// <summary>
    ///     Lightweight pass over the column and wall categories. Geometry
    ///     probing (location curve, bounding box) is deferred to
    ///     <see cref="ColumnWallRecordBuilder"/> so a large project only pays
    ///     the Solid access cost for the rows that actually ship on the page.
    /// </summary>
    /// <remarks>
    ///     Two categories feed the column path:
    ///     <list type="bullet">
    ///         <item><see cref="BuiltInCategory.OST_StructuralColumns"/> (always)</item>
    ///         <item><see cref="BuiltInCategory.OST_Columns"/> (only when the
    ///             request opts in via <see cref="ColumnWallRegisterExtractionOptions.IncludeArchitecturalColumns"/>)</item>
    ///     </list>
    ///     Walls always come from <see cref="BuiltInCategory.OST_Walls"/>.
    ///     Each surviving element becomes a <see cref="ColumnWallIdentity"/>
    ///     row; the builder promotes that row into a full record later.
    /// </remarks>
    public sealed class ColumnWallCollector
    {
        /// <summary>
        ///     Lightweight per-element entry collected during the model-wide
        ///     pass. Only fields that are cheap to read on the Revit thread are
        ///     populated here.
        /// </summary>
        public readonly struct ColumnWallIdentity
        {
            public ColumnWallIdentity(
                long elementId,
                string uniqueId,
                string documentKey,
                string linkInstanceUniqueId,
                LinkedDocumentContext linkContext,
                Element element,
                Document document,
                ElementKind kind)
            {
                ElementId = elementId;
                UniqueId = uniqueId;
                DocumentKey = documentKey;
                LinkInstanceUniqueId = linkInstanceUniqueId;
                LinkContext = linkContext;
                Element = element;
                Document = document;
                Kind = kind;
            }

            /// <summary>Revit integer element id (stringified through <see cref="ElementIdExtensions.GetValue"/>).</summary>
            public long ElementId { get; }

            /// <summary>Stable Revit unique id.</summary>
            public string UniqueId { get; }

            /// <summary>Stable hash key for the source document (host or linked).</summary>
            public string DocumentKey { get; }

            /// <summary>Unique id of the hosting Revit link instance, or null for host rows.</summary>
            public string LinkInstanceUniqueId { get; }

            /// <summary>Resolved link context when the element came from a loaded Revit link.</summary>
            public LinkedDocumentContext LinkContext { get; }

            /// <summary>The underlying Revit element (column or wall instance).</summary>
            public Element Element { get; }

            /// <summary>Document the element lives in.</summary>
            public Document Document { get; }

            /// <summary>Discriminator between structural/architectural columns and wall legs.</summary>
            public ElementKind Kind { get; }
        }

        /// <summary>
        ///     Discriminator used by the collector to route an element to the
        ///     appropriate record kind. Mirrors the wire DTO
        ///     <see cref="ColumnWallRecordKind"/> plus an architectural-column
        ///     bucket that the builder collapses onto <c>Column</c>.
        /// </summary>
        public enum ElementKind
        {
            StructuralColumn = 0,
            ArchitecturalColumn = 1,
            WallLeg = 2,
        }

        /// <summary>
        ///     Walk the host document and (optionally) loaded Revit links,
        ///     applying the same scope/policy predicates as the grid and beam
        ///     collectors. Returns a deterministic order (host then links,
        ///     within each list sorted by element id ascending).
        /// </summary>
        public List<ColumnWallIdentity> Collect(
            Document hostDocument,
            HostScopeResolver scope,
            bool includeLinkedModels,
            bool includeArchitecturalColumns,
            IReadOnlyList<string> requestedLevelIds,
            IReadOnlyList<string> requestedLevelNames,
            RPhaseFilter phaseFilter,
            RDesignOptionFilter designFilter)
        {
            if (hostDocument == null) throw new ArgumentNullException(nameof(hostDocument));
            if (scope == null) throw new ArgumentNullException(nameof(scope));

            var sink = new List<ColumnWallIdentity>(256);
            CollectFromDocument(
                hostDocument,
                scope,
                linkContext: null,
                includeArchitecturalColumns: includeArchitecturalColumns,
                requestedLevelIds: requestedLevelIds,
                requestedLevelNames: requestedLevelNames,
                phaseFilter: phaseFilter,
                designFilter: designFilter,
                sink: sink);

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
                            includeArchitecturalColumns: includeArchitecturalColumns,
                            requestedLevelIds: requestedLevelIds,
                            requestedLevelNames: requestedLevelNames,
                            phaseFilter: phaseFilter,
                            designFilter: designFilter,
                            sink: sink);
                    }
                    catch (InvalidOperationException)
                    {
                        // Some links fail to load mid-iteration; skip them to
                        // keep the rest of the extraction running.
                    }
                }
            }

            return sink;
        }

        /// <summary>
        ///     Resolve the requested level ids/names against the supplied
        ///     document and return the set of accepted level element ids. The
        ///     filter is resolved per document (host and each link) because
        ///     level element ids are document-local: a host level id never
        ///     matches a link level id, and reusing the host set would drop
        ///     every link row. A null/empty set means "all levels".
        /// </summary>
        /// <remarks>
        ///     Resolution warnings are intentionally discarded here: the
        ///     handler resolves the host scope once and reports those warnings;
        ///     re-reporting per link (where host ids are meaningless) would
        ///     produce duplicate, noisy entries.
        /// </remarks>
        private static HashSet<long> BuildLevelIdSet(
            Document document,
            IReadOnlyList<string> requestedLevelIds,
            IReadOnlyList<string> requestedLevelNames)
        {
            bool hasIds = requestedLevelIds != null && requestedLevelIds.Count > 0;
            bool hasNames = requestedLevelNames != null && requestedLevelNames.Count > 0;
            if (!hasIds && !hasNames) return null;

            var resolver = new LevelScopeResolver(document);
            var resolved = resolver.ResolveAll(requestedLevelIds, requestedLevelNames, null);
            if (resolved.Count == 0) return null;

            var set = new HashSet<long>();
            foreach (var level in resolved)
            {
                if (level == null) continue;
                set.Add(ElementIdAlias.GetValue(level.Id));
            }
            return set.Count > 0 ? set : null;
        }

        private static void CollectFromDocument(
            Document document,
            HostScopeResolver scope,
            LinkedDocumentContext linkContext,
            bool includeArchitecturalColumns,
            IReadOnlyList<string> requestedLevelIds,
            IReadOnlyList<string> requestedLevelNames,
            RPhaseFilter phaseFilter,
            RDesignOptionFilter designFilter,
            List<ColumnWallIdentity> sink)
        {
            if (document == null) return;
            string documentKey = linkContext?.LinkedDocumentKey ?? scope.HostDocumentKey;
            string linkInstanceUniqueId = linkContext?.LinkInstanceUniqueId;
            var levelIds = BuildLevelIdSet(document, requestedLevelIds, requestedLevelNames);

            CollectCategory(
                document,
                BuiltInCategory.OST_StructuralColumns,
                ElementKind.StructuralColumn,
                documentKey,
                linkInstanceUniqueId,
                linkContext,
                levelIds,
                phaseFilter,
                designFilter,
                sink);

            if (includeArchitecturalColumns)
            {
                CollectCategory(
                    document,
                    BuiltInCategory.OST_Columns,
                    ElementKind.ArchitecturalColumn,
                    documentKey,
                    linkInstanceUniqueId,
                    linkContext,
                    levelIds,
                    phaseFilter,
                    designFilter,
                    sink);
            }

            CollectCategory(
                document,
                BuiltInCategory.OST_Walls,
                ElementKind.WallLeg,
                documentKey,
                linkInstanceUniqueId,
                linkContext,
                levelIds,
                phaseFilter,
                designFilter,
                sink);
        }

        private static void CollectCategory(
            Document document,
            BuiltInCategory category,
            ElementKind kind,
            string documentKey,
            string linkInstanceUniqueId,
            LinkedDocumentContext linkContext,
            HashSet<long> levelIds,
            RPhaseFilter phaseFilter,
            RDesignOptionFilter designFilter,
            List<ColumnWallIdentity> sink)
        {
            FilteredElementCollector collector;
            try
            {
                collector = new FilteredElementCollector(document).OfCategory(category);
            }
            catch (InvalidOperationException)
            {
                // A category missing from this document raises; treat as
                // an empty result and let the rest of the extraction
                // continue.
                return;
            }

            foreach (var elem in collector)
            {
                if (elem == null) continue;
                if (phaseFilter != null && !phaseFilter.Accepts(elem)) continue;
                if (designFilter != null && !designFilter.Accepts(elem)) continue;

                if (levelIds != null && levelIds.Count > 0)
                {
                    long rowLevelId = ResolveRowLevelId(elem);
                    if (rowLevelId < 0) continue;
                    if (!levelIds.Contains(rowLevelId)) continue;
                }

                long elementIdValue = ElementIdAlias.GetValue(elem.Id);
                sink.Add(new ColumnWallIdentity(
                    elementIdValue,
                    elem.UniqueId,
                    documentKey,
                    linkInstanceUniqueId,
                    linkContext,
                    elem,
                    document,
                    kind));
            }
        }

        /// <summary>
        ///     Resolve the level id used for scope matching. Columns report
        ///     <c>FAMILY_LEVEL_PARAM</c> / <c>SCHEDULE_LEVEL_PARAM</c> /
        ///     <c>INSTANCE_REFERENCE_LEVEL_PARAM</c>; walls report
        ///     <c>WALL_BASE_CONSTRAINT</c> or <c>WALL_HEIGHT_TYPE</c>. The
        ///     helper walks the documented order and returns -1 when no
        ///     level-bearing parameter applies.
        /// </summary>
        private static long ResolveRowLevelId(Element element)
        {
            var p = ColumnWallRecordBuilder.ResolveLevelParameter(element);
            if (p == null) return -1;
            if (p.StorageType != StorageType.ElementId) return -1;
            var id = p.AsElementId();
            if (id == null || id == ElementId.InvalidElementId) return -1;
            return ElementIdAlias.GetValue(id);
        }
    }
}
