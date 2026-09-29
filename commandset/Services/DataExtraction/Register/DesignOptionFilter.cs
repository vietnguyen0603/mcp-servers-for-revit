using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Models.DataExtraction.Register;
using ElementIdAlias = RevitMCPCommandSet.Utils.ElementIdExtensions;

namespace RevitMCPCommandSet.Services.DataExtraction.Register
{
    /// <summary>
    ///     Design-option predicate. The filter resolves the active design
    ///     option for the requested policy and returns a function that
    ///     decides whether each element should be kept.
    /// </summary>
    public sealed class DesignOptionFilter
    {
        /// <summary>
        ///     Outcome of resolving the design-option filter for the request.
        /// </summary>
        public readonly struct Resolution
        {
            public Resolution(
                bool isEnabled,
                ElementId activeOptionId,
                string designOptionId,
                bool hasDesignOptions)
            {
                IsEnabled = isEnabled;
                ActiveOptionId = activeOptionId;
                DesignOptionId = designOptionId;
                HasDesignOptions = hasDesignOptions;
            }

            /// <summary>
            ///     True when the document actually uses design options.
            /// </summary>
            public bool IsEnabled { get; }

            /// <summary>
            ///     Active option id when the policy resolves to one.
            /// </summary>
            public ElementId ActiveOptionId { get; }

            /// <summary>
            ///     Stringified id, surfaced in the snapshot.
            /// </summary>
            public string DesignOptionId { get; }

            /// <summary>
            ///     True when the document declares design options.
            /// </summary>
            public bool HasDesignOptions { get; }
        }

        private readonly Resolution _resolution;

        private DesignOptionFilter(Resolution resolution)
        {
            _resolution = resolution;
        }

        public Resolution State => _resolution;

        /// <summary>
        ///     Resolve the design-option filter from the request. The
        ///     "active" policy requires a view id; when it is absent the
        ///     parser already raised an error before this method is called.
        /// </summary>
        public static DesignOptionFilter Resolve(
            Document document,
            View view,
            DesignOptionPolicy policy,
            List<WarningEntry> warnings = null)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            bool hasOptions = DocumentHasDesignOptions(document);
            if (!hasOptions)
            {
                return new DesignOptionFilter(new Resolution(false, null, null, false));
            }

            switch (policy)
            {
                case DesignOptionPolicy.All:
                    return new DesignOptionFilter(new Resolution(true, null, null, true));
                case DesignOptionPolicy.Primary:
                    return new DesignOptionFilter(new Resolution(true, null, null, true));
                case DesignOptionPolicy.Active:
                    if (view == null)
                    {
                        warnings?.Add(RegisterWarningFactory.ViewRequired(
                            "designOptionPolicy \"active\" requires a view to identify the active option."));
                        return new DesignOptionFilter(new Resolution(true, null, null, true));
                    }
                    ElementId activeId = null;
                    try
                    {
                        activeId = DesignOption.GetActiveDesignOptionId(document);
                    }
                    catch (InvalidOperationException)
                    {
                        activeId = null;
                    }
                    if (activeId == null || activeId == ElementId.InvalidElementId)
                    {
                        return new DesignOptionFilter(new Resolution(true, null, null, true));
                    }
                    string resolvedId = ElementIdAlias.GetValue(activeId).ToString();
                    return new DesignOptionFilter(new Resolution(true, activeId, resolvedId, true));
                default:
                    return new DesignOptionFilter(new Resolution(false, null, null, false));
            }
        }

        /// <summary>
        ///     Predicate that decides whether <paramref name="element"/>
        ///     survives the resolved design-option policy.
        /// </summary>
        public bool Accepts(Element element)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            if (!_resolution.IsEnabled) return true;

            var elementOption = element.get_Parameter(BuiltInParameter.DESIGN_OPTION_ID)?.AsElementId();
            if (_resolution.ActiveOptionId == null)
            {
                // "primary" or "all": when an element belongs to a secondary
                // option (option id != null/invalid), the primary policy
                // rejects it while the all policy accepts it.
                if (elementOption == null || elementOption == ElementId.InvalidElementId)
                {
                    return true;
                }
                // A specific option id is set. With primary, drop; with
                // all, keep. Both arrive here without ActiveOptionId set.
                return _resolution.DesignOptionId == null
                    ? false  // primary policy in effect
                    : true;
            }

            return elementOption == _resolution.ActiveOptionId;
        }

        private static bool DocumentHasDesignOptions(Document document)
        {
            // The "main model" is always present; what matters is whether
            // any non-primary option has been created.
            var options = new FilteredElementCollector(document)
                .OfClass(typeof(DesignOption))
                .Cast<DesignOption>();
            foreach (var option in options)
            {
                if (option == null) continue;
                if (!IsPrimaryOption(option))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsPrimaryOption(DesignOption option)
        {
            // IsPrimary exists on the class - reference it via the API.
            return option.IsPrimary;
        }
    }
}