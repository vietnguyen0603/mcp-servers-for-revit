using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.DataExtraction.Register;
using RevitMCPCommandSet.Services.DataExtraction.Register;
using RevitMCPCommandSet.Services.DataExtraction.GridExtraction;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    /// <summary>
    ///     Command that returns a <see cref="GridRegisterResponse"/>: one
    ///     record per Revit grid instance, with axis family, direction label,
    ///     signed coordinate (when applicable) and provenance.
    /// </summary>
    public class GetGridRegisterDataCommand : ExternalEventCommandBase
    {
        private readonly UIApplication _uiApplication;
        private GetGridRegisterDataEventHandler _handler => (GetGridRegisterDataEventHandler)Handler;

        public override string CommandName => "get_grid_register_data";

        public GetGridRegisterDataCommand(UIApplication uiApp)
            : base(new GetGridRegisterDataEventHandler(), uiApp)
        {
            _uiApplication = uiApp;
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                // Parse options first so warnings are surfaced even if
                // downstream steps fail.
                var options = RegisterRequestParser.ParseGrid(parameters, out var warnings);

                var document = _uiApplication?.ActiveUIDocument?.Document;
                string hostKey = RegisterKeyProvider.HostDocumentKey(
                    SafePathName(document),
                    document?.Title);
                var filterHash = FilterHashBuilder.ComputeGridHash(options);
                var revitVersion = SafeRevitVersion(_uiApplication);

                _handler.SetParameters(options, filterHash, hostKey, revitVersion);
                if (!RaiseAndWaitForCompletion(120000))
                {
                    throw new TimeoutException("Grid register data extraction timed out.");
                }
                var result = _handler.ResultInfo;
                if (result == null)
                {
                    return new GridRegisterResponse
                    {
                        SchemaVersion = CursorValidator.CurrentSchemaVersion,
                        Warnings = warnings,
                    };
                }
                if (warnings != null && warnings.Count > 0)
                {
                    result.Warnings = (result.Warnings ?? new List<WarningEntry>())
                        .Concat(warnings)
                        .ToList();
                }
                return result;
            }
            catch (RegisterRequestParser.RegisterParseException ex)
            {
                throw new Exception($"Failed to parse get_grid_register_data request: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to execute get_grid_register_data: {ex.Message}", ex);
            }
        }

        private static string SafePathName(Autodesk.Revit.DB.Document document)
        {
            try
            {
                return document?.PathName;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static string SafeRevitVersion(UIApplication uiApp)
        {
            try
            {
                return uiApp?.Application?.VersionNumber;
            }
            catch (InvalidOperationException)
            {
                return string.Empty;
            }
        }
    }
}