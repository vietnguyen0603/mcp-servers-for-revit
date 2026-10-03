using System.IO;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Exports views or sheets to image files. Revit appends the view type
    ///     and name to <c>fileNamePrefix</c>, so the written files are found by
    ///     comparing the folder before and after the export. No transaction.
    /// </summary>
    public class ExportViewImageEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Export View Image";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var folder = ExportFileUtils.PrepareFolder(parameters.Value<string>("folder"));

            var views = new List<View>();
            var notFound = new List<long>();
            var ids = parameters["viewIds"]?.ToObject<List<long>>() ?? new List<long>();
            if (ids.Count == 0)
            {
                views.Add(uiDoc.ActiveView);
            }
            else
            {
                foreach (var id in ids.Distinct())
                {
                    if (doc.GetElement(id.ToRevitElementId()) is View view && !view.IsTemplate)
                        views.Add(view);
                    else
                        notFound.Add(id);
                }
            }

            if (views.Count == 0)
                return Fail($"No exportable views. Not found: {string.Join(", ", notFound)}.");

            var prefix = ExportFileUtils.Sanitize(parameters.Value<string>("fileNamePrefix"));
            if (string.IsNullOrEmpty(prefix))
                prefix = ExportFileUtils.Sanitize(doc.Title);
            if (string.IsNullOrEmpty(prefix))
                prefix = "export";

            var fileType = ParseImageType(parameters.Value<string>("format"));
            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = Path.Combine(folder, prefix),
                HLRandWFViewsFileType = fileType,
                ShadowViewsFileType = fileType,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = Math.Max(64, Math.Min(15000, parameters.Value<int?>("pixelSize") ?? 2048)),
                FitDirection = DocumentationUtils.ParseEnum(parameters.Value<string>("fitDirection"),
                    FitDirectionType.Horizontal)
            };
            options.SetViewsAndSheets(views.Select(v => v.Id).ToList());

            var before = ExportFileUtils.Snapshot(folder);
            doc.ExportImage(options);
            var files = ExportFileUtils.ChangedFiles(folder, before)
                .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (files.Count == 0)
                return Fail("Revit did not write any image files.");

            return Ok($"Exported {files.Count} image(s) to {folder}.", new
            {
                folder,
                viewIds = views.Select(v => v.Id.GetValue()).ToList(),
                files,
                notFound = notFound.Count > 0 ? notFound : null
            });
        }

        private static ImageFileType ParseImageType(string format)
        {
            switch ((format ?? "PNG").Trim().ToUpperInvariant())
            {
                case "PNG":
                    return ImageFileType.PNG;
                case "JPG":
                case "JPEG":
                    return ImageFileType.JPEGLossless;
                case "BMP":
                    return ImageFileType.BMP;
                case "TIF":
                case "TIFF":
                    return ImageFileType.TIFF;
                default:
                    throw new ArgumentException($"Unsupported image format '{format}'. Use PNG, JPEG, BMP or TIFF.");
            }
        }
    }
}
