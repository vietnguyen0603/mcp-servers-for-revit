using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Services.DataExtraction.Register
{
    /// <summary>
    ///     Wraps a single Revit link instance, capturing the host-side
    ///     document key, the link's unique id, and the total transform that
    ///     maps the link's internal coordinates into the host's project
    ///     coordinates.
    /// </summary>
    public sealed class LinkedDocumentContext
    {
        public LinkedDocumentContext(
            Document linkDocument,
            RevitLinkInstance linkInstance,
            string hostDocumentKey,
            string linkedDocumentKey)
        {
            LinkDocument = linkDocument ?? throw new ArgumentNullException(nameof(linkDocument));
            LinkInstance = linkInstance ?? throw new ArgumentNullException(nameof(linkInstance));
            HostDocumentKey = hostDocumentKey ?? throw new ArgumentNullException(nameof(hostDocumentKey));
            LinkedDocumentKey = linkedDocumentKey ?? throw new ArgumentNullException(nameof(linkedDocumentKey));
            try
            {
                TotalTransform = linkInstance.GetTotalTransform();
            }
            catch (InvalidOperationException)
            {
                // Some link configurations do not expose a transform yet; fall back to identity.
                TotalTransform = Transform.Identity;
            }
        }

        public Document LinkDocument { get; }

        public RevitLinkInstance LinkInstance { get; }

        public string HostDocumentKey { get; }

        public string LinkedDocumentKey { get; }

        /// <summary>
        ///     Transform that maps the link's internal coordinates into the
        ///     host's project coordinates. Identity when the API refuses to
        ///     expose a transform.
        /// </summary>
        public Transform TotalTransform { get; }

        public string LinkInstanceUniqueId => LinkInstance.UniqueId;

        public string LinkInstanceName => LinkInstance.Name ?? string.Empty;
    }

    /// <summary>
    ///     Resolves the collection scope for a register request. The resolver
    ///     inspects the host document and any loaded Revit links, deciding
    ///     which documents to iterate based on
    ///     <see cref="RevitMCPCommandSet.Models.DataExtraction.Register.RegisterExtractionOptions.IncludeLinkedModels"/>
    ///     and the optional view scope.
    /// </summary>
    public sealed class HostScopeResolver
    {
        private readonly string _hostDocumentKey;

        public HostScopeResolver(Document hostDocument, string hostDocumentKey)
        {
            HostDocument = hostDocument ?? throw new ArgumentNullException(nameof(hostDocument));
            _hostDocumentKey = hostDocumentKey ?? throw new ArgumentNullException(nameof(hostDocumentKey));
        }

        public Document HostDocument { get; }

        public string HostDocumentKey => _hostDocumentKey;

        /// <summary>
        ///     Enumerate the linked Revit link instances currently loaded in
        ///     the host document. Returns an empty list when
        ///     <paramref name="includeLinkedModels"/> is false.
        /// </summary>
        public IReadOnlyList<LinkedDocumentContext> ResolveLinkedContexts(bool includeLinkedModels)
        {
            if (!includeLinkedModels) return Array.Empty<LinkedDocumentContext>();

            var contexts = new List<LinkedDocumentContext>();
            var collector = new FilteredElementCollector(HostDocument)
                .OfClass(typeof(RevitLinkInstance));
            foreach (var element in collector)
            {
                if (element is not RevitLinkInstance linkInstance) continue;
                var linkDoc = linkInstance.GetLinkDocument();
                if (linkDoc == null) continue;
                var linkKey = RegisterKeyProvider.LinkedDocumentKey(
                    _hostDocumentKey,
                    linkInstance.UniqueId,
                    linkDoc.Title ?? string.Empty);
                contexts.Add(new LinkedDocumentContext(linkDoc, linkInstance, _hostDocumentKey, linkKey));
            }
            return contexts;
        }

        /// <summary>
        ///     Build a <see cref="FilteredElementCollector"/> for the host
        ///     document. When <paramref name="view"/> is non-null the
        ///     collector is scoped to that view; otherwise it collects
        ///     document-wide.
        /// </summary>
        public FilteredElementCollector CreateHostCollector(View view)
        {
            return view != null
                ? new FilteredElementCollector(HostDocument, view.Id)
                : new FilteredElementCollector(HostDocument);
        }

        /// <summary>
        ///     Build a <see cref="FilteredElementCollector"/> for a linked
        ///     document. Linked documents cannot be scoped to a host view;
        ///     collection is document-wide and the caller is expected to
        ///     apply view-level filtering through
        ///     <see cref="Autodesk.Revit.DB.FilteredElementCollector.IsViewValidForElement"/>
        ///     or equivalent.
        /// </summary>
        public static FilteredElementCollector CreateLinkCollector(LinkedDocumentContext context, View hostView)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            var collector = new FilteredElementCollector(context.LinkDocument);
            if (hostView != null)
            {
                // Exclude elements that are not visible in the host view.
                collector = collector.WherePasses(new VisibleInViewFilter(context.LinkDocument, hostView.Id));
            }
            return collector;
        }
    }
}