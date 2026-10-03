using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Library
{
    /// <summary>Helpers for copying view-specific content between views and open documents.</summary>
    internal static class ViewCopyUtils
    {
        /// <summary>
        ///     Finds an open, non-linked document by title, with or without the
        ///     ".rvt" extension, or by file name. Null title = the active document.
        /// </summary>
        public static Document FindDocument(Autodesk.Revit.ApplicationServices.Application app, Document active,
            string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return active;

            var wanted = StripExtension(title.Trim());
            var open = app.Documents.Cast<Document>().Where(d => !d.IsLinked).ToList();
            var match = open.FirstOrDefault(d => string.Equals(StripExtension(d.Title), wanted, StringComparison.OrdinalIgnoreCase))
                        ?? open.FirstOrDefault(d => !string.IsNullOrEmpty(d.PathName)
                                                    && string.Equals(StripExtension(System.IO.Path.GetFileName(d.PathName)), wanted,
                                                        StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;

            throw new ArgumentException(
                $"No open document titled '{title}'. Open documents: {string.Join(", ", open.Select(d => d.Title))}.");
        }

        private static string StripExtension(string name)
        {
            return name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
        }

        /// <summary>
        ///     View-owned elements that can be copied view-to-view: excludes the
        ///     view itself, its sketch plane, crop/extent elements, view markers,
        ///     sketch lines owned by other elements, nested family
        ///     sub-components and group members (their group is copied instead).
        ///     Live view references are skipped when copying to another document.
        /// </summary>
        public static List<ElementId> CopyableElements(Document doc, View view, bool crossDocument)
        {
            return new FilteredElementCollector(doc, view.Id)
                .WhereElementIsNotElementType()
                .Where(e => e.OwnerViewId == view.Id)
                .Where(e => IsCopyable(e, crossDocument))
                .Select(e => e.Id)
                .OrderBy(id => id.GetValue())
                .ToList();
        }

        private static bool IsCopyable(Element element, bool crossDocument)
        {
            if (element is View || element is Viewport || element is SketchPlane)
                return false;
            if (element.Category == null)
                return false;
            if (element.GroupId != null && element.GroupId != ElementId.InvalidElementId)
                return false;
            if (element is FamilyInstance fi && fi.SuperComponent != null)
                return false;

            switch ((BuiltInCategory)(int)element.Category.Id.GetValue())
            {
                case BuiltInCategory.OST_Viewers:
                case BuiltInCategory.OST_SketchLines:
                case BuiltInCategory.OST_IOSSketchGrid:
                    return false;
                case BuiltInCategory.OST_ReferenceViewer:
                    // Live view references cannot point into another document.
                    return !crossDocument;
                default:
                    return true;
            }
        }

        public static CopyPasteOptions DestinationTypeOptions()
        {
            var options = new CopyPasteOptions();
            options.SetDuplicateTypeNamesHandler(new HandleDuplicateTypeUtils());
            return options;
        }
    }
}
