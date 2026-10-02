using System.IO;
using System.Text.RegularExpressions;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Exports sheets or views to PDF (Revit 2022+) or DWG. Each target is
    ///     exported separately so its file name follows <c>fileNameTemplate</c>;
    ///     a combined PDF is exported in one call. Files actually written are
    ///     detected by comparing the folder before and after each export.
    ///     Export does not modify the model, so no transaction is opened.
    /// </summary>
    public class ExportSheetsEventHandler : JsonParameterEventHandler
    {
        private const string DefaultTemplate = "{number} - {name}";

        public override string GetName() => "Export Sheets";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var format = (parameters.Value<string>("format") ?? string.Empty).Trim().ToUpperInvariant();
            if (format != "PDF" && format != "DWG")
                return Fail("'format' must be PDF or DWG.");

            var folder = ExportFileUtils.PrepareFolder(parameters.Value<string>("folder"));
            var targets = ResolveTargets(doc, parameters, out var unresolved);
            if (targets.Count == 0)
                return Fail("No sheets or views to export. Provide sheetIds, sheetNumbers or viewIds." +
                            (unresolved.Count > 0 ? $" Not found: {string.Join(", ", unresolved)}." : string.Empty));

            var template = parameters.Value<string>("fileNameTemplate");
            if (string.IsNullOrWhiteSpace(template))
                template = DefaultTemplate;

            List<object> results;
            if (format == "PDF")
            {
#if REVIT2022_OR_GREATER
                results = ExportPdf(doc, parameters, folder, targets, template);
#else
                return Fail("PDF export through the Revit API requires Revit 2022 or later. Use format DWG or print from Revit.");
#endif
            }
            else
            {
                results = ExportDwg(doc, parameters, folder, targets, template);
            }

            var written = results.Sum(r => ((JObject)JObject.FromObject(r))["files"]?.Count() ?? 0);
            return Ok($"Exported {targets.Count} item(s) to {format}; {written} file(s) written to {folder}.", new
            {
                format,
                folder,
                results,
                notFound = unresolved.Count > 0 ? unresolved : null
            });
        }

        private static List<View> ResolveTargets(Document doc, JObject parameters, out List<string> unresolved)
        {
            unresolved = new List<string>();
            var targets = new List<View>();

            foreach (var id in parameters["sheetIds"]?.ToObject<List<long>>() ?? new List<long>())
            {
                if (doc.GetElement(id.ToRevitElementId()) is ViewSheet sheet)
                    targets.Add(sheet);
                else
                    unresolved.Add($"sheetId {id}");
            }

            var numbers = parameters["sheetNumbers"]?.ToObject<List<string>>() ?? new List<string>();
            if (numbers.Count > 0)
            {
                var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Where(s => !s.IsTemplate).ToList();
                foreach (var number in numbers)
                {
                    var sheet = sheets.FirstOrDefault(s =>
                        string.Equals(s.SheetNumber, number?.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (sheet != null)
                        targets.Add(sheet);
                    else
                        unresolved.Add($"sheet number '{number}'");
                }
            }

            foreach (var id in parameters["viewIds"]?.ToObject<List<long>>() ?? new List<long>())
            {
                if (doc.GetElement(id.ToRevitElementId()) is View view && !view.IsTemplate && view.CanBePrinted)
                    targets.Add(view);
                else
                    unresolved.Add($"viewId {id} (not a printable view)");
            }

            return targets.GroupBy(v => v.Id.GetValue()).Select(g => g.First()).ToList();
        }

        private static string FileNameFor(View view, string template)
        {
            var sheet = view as ViewSheet;
            var name = Regex.Replace(template, @"\{(number|name|viewType)\}", match =>
            {
                switch (match.Groups[1].Value)
                {
                    case "number":
                        return sheet?.SheetNumber ?? string.Empty;
                    case "name":
                        return view.Name;
                    default:
                        return view.ViewType.ToString();
                }
            }, RegexOptions.IgnoreCase);

            name = ExportFileUtils.Sanitize(name).Trim(' ', '-', '_', '.');
            return string.IsNullOrEmpty(name) ? $"view-{view.Id.GetValue()}" : name;
        }

#if REVIT2022_OR_GREATER
        private static List<object> ExportPdf(Document doc, JObject parameters, string folder, List<View> targets,
            string template)
        {
            var options = BuildPdfOptions(parameters["pdf"] as JObject);
            var results = new List<object>();

            if (parameters.Value<bool?>("combine") == true)
            {
                var combinedName = parameters.Value<string>("combinedFileName");
                options.Combine = true;
                options.FileName = ExportFileUtils.Sanitize(string.IsNullOrWhiteSpace(combinedName)
                    ? $"{doc.Title} - Combined"
                    : combinedName.Trim());
                var ids = targets.Select(v => v.Id).ToList();
                results.Add(ExportFileUtils.Capture(folder, () => doc.Export(folder, ids, options),
                    targets.Select(v => (object)v.Id.GetValue()).ToList(), options.FileName));
                return results;
            }

            options.Combine = true; // one target per call, so the file name is exactly FileName
            foreach (var view in targets)
            {
                options.FileName = FileNameFor(view, template);
                results.Add(ExportFileUtils.Capture(folder, () => doc.Export(folder, new List<ElementId> { view.Id }, options),
                    new List<object> { view.Id.GetValue() }, options.FileName));
            }

            return results;
        }

        private static PDFExportOptions BuildPdfOptions(JObject pdf)
        {
            var options = new PDFExportOptions();
            if (pdf == null)
                return options;

            var placement = pdf.Value<string>("paperPlacement");
            if (!string.IsNullOrWhiteSpace(placement))
                options.PaperPlacement = DocumentationUtils.ParseEnum(placement, PaperPlacementType.Center);

            var zoom = pdf["zoom"];
            if (zoom != null && zoom.Type != JTokenType.Null)
            {
                if (zoom.Type == JTokenType.Integer || zoom.Type == JTokenType.Float)
                {
                    options.ZoomType = ZoomType.Zoom;
                    options.ZoomPercentage = Math.Max(1, Math.Min(10000, (int)Math.Round(zoom.Value<double>())));
                }
                else
                {
                    options.ZoomType = ZoomType.FitToPage;
                }
            }

            var color = pdf.Value<string>("colorDepth");
            if (!string.IsNullOrWhiteSpace(color))
                options.ColorDepth = DocumentationUtils.ParseEnum(color, ColorDepthType.Color);

            var raster = pdf.Value<string>("rasterQuality");
            if (!string.IsNullOrWhiteSpace(raster))
                options.RasterQuality = DocumentationUtils.ParseEnum(raster, RasterQualityType.High);

            if (pdf.Value<bool?>("hideCropBoundaries") is bool hideCrop)
                options.HideCropBoundaries = hideCrop;
            if (pdf.Value<bool?>("hideScopeBoxes") is bool hideScope)
                options.HideScopeBoxes = hideScope;
            if (pdf.Value<bool?>("hideUnreferencedViewTags") is bool hideTags)
                options.HideUnreferencedViewTags = hideTags;

            return options;
        }
#endif

        private static List<object> ExportDwg(Document doc, JObject parameters, string folder, List<View> targets,
            string template)
        {
            var dwg = parameters["dwg"] as JObject;
            var setupName = dwg?.Value<string>("exportSetupName");
            DWGExportOptions options;
            if (!string.IsNullOrWhiteSpace(setupName))
            {
                options = DWGExportOptions.GetPredefinedOptions(doc, setupName.Trim())
                          ?? throw new ArgumentException($"DWG export setup '{setupName}' not found.");
            }
            else
            {
                options = new DWGExportOptions();
            }

            if (dwg?.Value<bool?>("mergedViews") is bool merged)
                options.MergedViews = merged;

            var results = new List<object>();
            foreach (var view in targets)
            {
                var name = FileNameFor(view, template);
                results.Add(ExportFileUtils.Capture(folder,
                    () => doc.Export(folder, name, new List<ElementId> { view.Id }, options),
                    new List<object> { view.Id.GetValue() }, name));
            }

            return results;
        }
    }
}
