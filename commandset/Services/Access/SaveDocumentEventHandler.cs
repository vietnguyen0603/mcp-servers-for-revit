using System.IO;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Services.Access
{
    /// <summary>
    ///     save_document: saves the active document in place, or Save As to an
    ///     absolute path (overwrite, compact, asCentral for workshared models).
    ///     Runs outside any transaction, as Revit requires for saving.
    /// </summary>
    public class SaveDocumentEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Save Document";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var saveAsPath = parameters.Value<string>("saveAsPath");
            var compact = parameters.Value<bool?>("compact") ?? false;
            var overwrite = parameters.Value<bool?>("overwrite") ?? false;
            var asCentral = parameters.Value<bool?>("asCentral") ?? false;
            var warnings = new JArray();

            if (doc.IsModifiable)
                return Fail("The document has an open transaction or edit mode; finish it before saving.");
            if (doc.IsReadOnly)
                return Fail("The document is read-only.");

            if (string.IsNullOrWhiteSpace(saveAsPath))
            {
                if (string.IsNullOrEmpty(doc.PathName))
                    return Fail("The document has never been saved; give saveAsPath.");
                try
                {
                    doc.Save(new SaveOptions { Compact = compact });
                }
                catch (Exception ex)
                {
                    return Fail($"Save failed: {ex.Message}");
                }
            }
            else
            {
                string path;
                try
                {
                    path = Path.GetFullPath(saveAsPath.Trim());
                }
                catch (Exception ex)
                {
                    return Fail($"Invalid saveAsPath '{saveAsPath}': {ex.Message}");
                }

                if (File.Exists(path) && !overwrite)
                    return Fail($"'{path}' already exists; pass overwrite: true to replace it.");
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                    Directory.CreateDirectory(folder);

                var options = new SaveAsOptions { OverwriteExistingFile = overwrite, Compact = compact };
                if (doc.IsWorkshared)
                    options.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = asCentral });
                else if (asCentral)
                    warnings.Add("asCentral ignored: the document is not workshared.");

                try
                {
                    doc.SaveAs(path, options);
                }
                catch (Exception ex)
                {
                    var hint = doc.IsWorkshared && !asCentral
                        ? " For a workshared (e.g. detached) model pass asCentral: true."
                        : string.Empty;
                    return Fail($"Save As to '{path}' failed: {ex.Message}{hint}");
                }
            }

            var result = new JObject
            {
                ["title"] = doc.Title,
                ["path"] = string.IsNullOrEmpty(doc.PathName) ? null : doc.PathName,
                ["savedAs"] = !string.IsNullOrWhiteSpace(saveAsPath),
                ["compact"] = compact,
                ["isWorkshared"] = doc.IsWorkshared,
                ["isModified"] = doc.IsModified
            };
            if (warnings.Count > 0)
                result["warnings"] = warnings;
            return Ok($"Saved '{doc.Title}' to '{doc.PathName}'.", result);
        }
    }
}
