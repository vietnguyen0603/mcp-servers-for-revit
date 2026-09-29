using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.DataExtraction.Register;
using RevitMCPCommandSet.Services.DataExtraction.ColumnWallExtraction;
using RevitMCPCommandSet.Services.DataExtraction.Register;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    /// <summary>
    ///     External-event command that returns the structural column and
    ///     wall register for the active Revit document. The command name
    ///     matches the <c>get_column_wall_register_data</c> entry the server
    ///     invokes; the snake case spelling is part of the MCP contract.
    /// </summary>
    public class GetColumnWallRegisterDataCommand : ExternalEventCommandBase
    {
        private readonly UIApplication _uiApplication;
        private GetColumnWallRegisterDataEventHandler _handler
            => (GetColumnWallRegisterDataEventHandler)Handler;

        /// <summary>
        ///     Wire command name. Must match the value published in
        ///     <c>server/src/tools/get_column_wall_register_data.ts</c>.
        /// </summary>
        public override string CommandName => "get_column_wall_register_data";

        public GetColumnWallRegisterDataCommand(UIApplication uiApp)
            : base(new GetColumnWallRegisterDataEventHandler(), uiApp)
        {
            _uiApplication = uiApp;
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                // Parse options first so warnings are surfaced even if
                // downstream steps fail.
                var options = RegisterRequestParser.ParseColumnWall(parameters, out var warnings);

                var document = _uiApplication?.ActiveUIDocument?.Document;
                string hostKey = RegisterKeyProvider.HostDocumentKey(
                    SafePathName(document),
                    document?.Title);
                var filterHash = FilterHashBuilder.ComputeColumnWallHash(options);
                var revitVersion = SafeRevitVersion(_uiApplication);

                _handler.SetParameters(options, filterHash, hostKey, revitVersion);

                // Column/wall extraction iterates over the whole project and
                // may run for several seconds on large models; allocate a
                // generous timeout so paging responses have a chance to
                // flush.
                if (!RaiseAndWaitForCompletion(120000))
                {
                    throw new TimeoutException("Column/wall register extraction timed out.");
                }

                var result = _handler.ResultInfo;
                if (result == null)
                {
                    return new ColumnWallRegisterResponse
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
                throw new Exception($"Failed to parse get_column_wall_register_data request: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to execute get_column_wall_register_data: {ex.Message}", ex);
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
