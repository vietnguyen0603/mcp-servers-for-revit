using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction.Register;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.DataExtraction.Register
{
    /// <summary>
    ///     Validates that a Revit active document is available and that the
    ///     view id requested on the wire (when present) resolves to an
    ///     actual <see cref="View"/> in that document.
    /// </summary>
    /// <remarks>
    ///     Validation here is intentionally narrow: it only confirms that the
    ///     basic preconditions for a register extraction are met. Domain
    ///     rules (level ids, phase ids, design options) are checked by their
    ///     respective resolvers so that warnings can flow into the response
    ///     envelope instead of aborting the request.
    /// </remarks>
    public static class ActiveDocumentValidator
    {
        /// <summary>
        ///     Result of validating the active document.
        /// </summary>
        public readonly struct ValidationResult
        {
            public ValidationResult(Document document, View view, List<WarningEntry> warnings)
            {
                Document = document;
                View = view;
                Warnings = warnings ?? new List<WarningEntry>();
            }

            public Document Document { get; }
            public View View { get; }
            public List<WarningEntry> Warnings { get; }

            public bool IsValid => Document != null;
        }

        /// <summary>
        ///     Validate the host document and (optionally) the requested view.
        ///     When the view id is null or empty the <see cref="ValidationResult.View"/>
        ///     is left null and the handler collects elements document-wide.
        /// </summary>
        public static ValidationResult Validate(UIApplication uiApp, string viewId)
        {
            var warnings = new List<WarningEntry>();
            if (uiApp == null)
            {
                warnings.Add(RegisterWarningFactory.NoActiveDocument());
                return new ValidationResult(null, null, warnings);
            }

            Document doc = null;
            try
            {
                var uidoc = uiApp.ActiveUIDocument;
                doc = uidoc?.Document;
            }
            catch (InvalidOperationException)
            {
                // No active document.
            }

            if (doc == null)
            {
                warnings.Add(RegisterWarningFactory.NoActiveDocument());
                return new ValidationResult(null, null, warnings);
            }

            View view = null;
            if (!string.IsNullOrEmpty(viewId))
            {
                view = TryResolveView(doc, viewId);
                if (view == null)
                {
                    warnings.Add(RegisterWarningFactory.ViewNotFound(viewId));
                }
            }
            return new ValidationResult(doc, view, warnings);
        }

        private static View TryResolveView(Document doc, string viewId)
        {
            if (long.TryParse(viewId, out var numeric))
            {
                var id = numeric.ToRevitElementId();
                var element = doc.GetElement(id);
                if (element is View v) return v;
            }
            // Fallback: some clients serialise unique ids.
            var byUnique = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .FirstOrDefault(e => e.UniqueId == viewId);
            return byUnique as View;
        }
    }
}