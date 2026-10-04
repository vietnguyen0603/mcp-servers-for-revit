using System.IO;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Services.Access
{
    /// <summary>
    ///     open_document: opens a model (optionally detached from central, optionally
    ///     saved as a copy first) or creates a new project from a template, and
    ///     activates it in the UI with UIApplication.OpenAndActivateDocument. Works
    ///     without an active document. Runs in the external event outside any
    ///     transaction, which OpenAndActivateDocument requires.
    /// </summary>
    public class OpenDocumentEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Open Document";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            return RunWithApplication(uiDoc.Application, parameters);
        }

        protected override AIResult<object> RunWithApplication(UIApplication uiApp, JObject parameters)
        {
            var app = uiApp.Application;
            var path = parameters.Value<string>("path");
            var templatePath = parameters.Value<string>("templatePath");
            var saveAsPath = parameters.Value<string>("saveAsPath");
            var overwrite = parameters.Value<bool?>("overwrite") ?? false;
            var activate = parameters.Value<bool?>("activate") ?? true;
            var detach = parameters.Value<bool?>("detach") ?? false;
            var warnings = new JArray();

            var active = uiApp.ActiveUIDocument?.Document;
            if (activate && active != null && active.IsModifiable)
                return Fail("The active document has an open transaction or edit mode; finish it before opening another document.");

            string target = null;
            if (!string.IsNullOrWhiteSpace(saveAsPath))
            {
                if (!TryFullPath(saveAsPath, out target, out var error))
                    return Fail($"Invalid saveAsPath: {error}");
                if (File.Exists(target) && !overwrite)
                    return Fail($"'{target}' already exists; pass overwrite: true to replace it.");
                if (FindOpen(app, target) != null)
                    return Fail($"'{target}' is open in this session; close it or choose another saveAsPath.");
            }

            Document document;
            var created = false;
            var detached = false;
            try
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    if (!TryFullPath(path, out var source, out var error))
                        return Fail($"Invalid path: {error}");
                    if (!File.Exists(source))
                        return Fail($"File not found: '{source}'.");

                    var alreadyOpen = FindOpen(app, source);
                    if (alreadyOpen != null)
                    {
                        if (target != null)
                            return Fail($"'{source}' is already open; use save_document with saveAsPath to save a copy of it.");
                        if (detach)
                            warnings.Add("The model is already open; detach ignored.");
                        document = activate ? uiApp.OpenAndActivateDocument(source).Document : alreadyOpen;
                    }
                    else
                    {
                        var options = new OpenOptions();
                        BasicFileInfo info = null;
                        try
                        {
                            info = BasicFileInfo.Extract(source);
                        }
                        catch (Exception)
                        {
                            // Not readable as a Revit file header; let the open report the problem.
                        }

                        if (detach)
                        {
                            if (info != null && info.IsWorkshared)
                            {
                                options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                                detached = true;
                            }
                            else
                            {
                                warnings.Add("The model is not workshared; detach ignored.");
                            }
                        }
                        else if (info != null && info.IsWorkshared && info.IsCentral && target == null)
                        {
                            warnings.Add("This is a central model opened directly; use detach: true (with saveAsPath) to work on a detached copy.");
                        }

                        var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(source);
                        if (target != null)
                        {
                            var opened = app.OpenDocumentFile(modelPath, options);
                            SaveAs(opened, target, overwrite, warnings);
                            document = activate ? Reopen(uiApp, opened, target, warnings) : opened;
                        }
                        else
                        {
                            document = activate
                                ? uiApp.OpenAndActivateDocument(modelPath, options, false).Document
                                : app.OpenDocumentFile(modelPath, options);
                        }
                    }
                }
                else
                {
                    var template = string.IsNullOrWhiteSpace(templatePath) ? app.DefaultProjectTemplate : templatePath.Trim();
                    if (target == null)
                        return Fail("A new project needs saveAsPath.");
                    Document newDocument;
                    if (string.IsNullOrWhiteSpace(template))
                    {
                        newDocument = app.NewProjectDocument(UnitSystem.Metric);
                        warnings.Add("Revit has no default project template; created a project without a template (metric).");
                    }
                    else
                    {
                        if (!File.Exists(template))
                            return Fail($"Template not found: '{template}'. Give templatePath (an .rte file).");
                        newDocument = app.NewProjectDocument(template);
                    }

                    created = true;
                    SaveAs(newDocument, target, overwrite, warnings);
                    document = activate ? Reopen(uiApp, newDocument, target, warnings) : newDocument;
                }
            }
            catch (Exception ex)
            {
                return Fail($"open_document failed: {ex.Message}");
            }

            var isActive = uiApp.ActiveUIDocument?.Document?.Equals(document) ?? false;
            if (!activate)
                warnings.Add("Opened in the background: the other tools work on the active document only.");

            var result = new JObject
            {
                ["title"] = document.Title,
                ["path"] = string.IsNullOrEmpty(document.PathName) ? null : document.PathName,
                ["created"] = created,
                ["detached"] = detached,
                ["isActive"] = isActive,
                ["isWorkshared"] = document.IsWorkshared,
                ["isFamilyDocument"] = document.IsFamilyDocument,
                ["isModified"] = document.IsModified
            };
            if (warnings.Count > 0)
                result["warnings"] = warnings;
            return Ok($"{(created ? "Created" : "Opened")} '{document.Title}'{(isActive ? " and activated it" : string.Empty)}.", result);
        }

        /// <summary>Save As (a workshared document becomes a new central, as Revit requires for detached models).</summary>
        private static void SaveAs(Document document, string target, bool overwrite, JArray warnings)
        {
            var folder = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            var options = new SaveAsOptions { OverwriteExistingFile = overwrite };
            if (document.IsWorkshared)
            {
                options.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
                warnings.Add($"The workshared copy was saved as a new central model at '{target}'.");
            }
            document.SaveAs(target, options);
        }

        /// <summary>Closes a background document and opens its saved file in the UI as the active document.</summary>
        private static Document Reopen(UIApplication uiApp, Document background, string target, JArray warnings)
        {
            if (!background.Close(false))
                warnings.Add("Revit kept the background copy open.");
            return uiApp.OpenAndActivateDocument(target).Document;
        }

        private static Document FindOpen(Autodesk.Revit.ApplicationServices.Application app, string fullPath)
        {
            foreach (Document open in app.Documents)
            {
                if (open.IsLinked || string.IsNullOrEmpty(open.PathName))
                    continue;
                if (string.Equals(SafeFullPath(open.PathName), fullPath, StringComparison.OrdinalIgnoreCase))
                    return open;
            }
            return null;
        }

        private static string SafeFullPath(string path)
        {
            return TryFullPath(path, out var full, out _) ? full : path;
        }

        private static bool TryFullPath(string path, out string full, out string error)
        {
            try
            {
                full = Path.GetFullPath(path.Trim());
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                full = null;
                error = ex.Message;
                return false;
            }
        }
    }
}
