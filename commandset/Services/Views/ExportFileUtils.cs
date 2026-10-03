using System.IO;
using System.Text.RegularExpressions;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>File-system helpers shared by the export handlers.</summary>
    internal static class ExportFileUtils
    {
        private static readonly Regex AbsoluteWindowsPath = new Regex(@"^([A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+)");

        public static string PrepareFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !AbsoluteWindowsPath.IsMatch(folder.Trim()))
                throw new ArgumentException("'folder' must be an absolute path such as C:\\Exports.");
            if (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                throw new ArgumentException("'folder' contains invalid path characters.");

            var full = Path.GetFullPath(folder.Trim());
            Directory.CreateDirectory(full);
            return full;
        }

        public static string Sanitize(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = (name ?? string.Empty).Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            return new string(chars).Trim();
        }

        public static Dictionary<string, DateTime> Snapshot(string folder)
        {
            return Directory.GetFiles(folder).ToDictionary(f => f, File.GetLastWriteTimeUtc, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Files created or rewritten in <paramref name="folder" /> since <paramref name="before" />.</summary>
        public static List<string> ChangedFiles(string folder, Dictionary<string, DateTime> before)
        {
            return Directory.GetFiles(folder)
                .Where(f => !before.TryGetValue(f, out var time) || File.GetLastWriteTimeUtc(f) != time)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Runs one export and reports the files it wrote.</summary>
        public static object Capture(string folder, Func<bool> export, List<object> ids, string fileName)
        {
            var before = Snapshot(folder);
            bool ok;
            string error = null;
            try
            {
                ok = export();
            }
            catch (Exception ex)
            {
                ok = false;
                error = ex.Message;
            }

            var files = ChangedFiles(folder, before);
            return new
            {
                ids,
                fileName,
                success = ok && files.Count > 0,
                files,
                message = error ?? (ok ? (files.Count == 0 ? "Revit reported success but no file was written." : null)
                    : "Revit reported the export failed.")
            };
        }
    }
}
