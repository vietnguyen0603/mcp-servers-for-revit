using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.DataExtraction.Register;
using RevitMCPCommandSet.Services.DataExtraction.Register;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    /// <summary>
    ///     External-event command that returns the structural beam register for
    ///     the active Revit document. The command name matches the
    ///     <c>get_beam_register_data</c> entry the server invokes; the snake
    ///     case spelling is part of the MCP contract.
    /// </summary>
    public class GetBeamRegisterDataCommand : ExternalEventCommandBase
    {
        private GetBeamRegisterDataEventHandler _handler => (GetBeamRegisterDataEventHandler)Handler;

        /// <summary>
        ///     Wire command name. Must match the value published in
        ///     <c>server/src/tools/get_beam_register_data.ts</c>.
        /// </summary>
        public override string CommandName => "get_beam_register_data";

        public GetBeamRegisterDataCommand(UIApplication uiApp)
            : base(new GetBeamRegisterDataEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                // Parse via the shared beam parser so request validation,
                // defaults, and warning surfacing follow the documented
                // contract. The parser tolerates a missing params token by
                // applying defaults.
                BeamRegisterExtractionOptions options;
                try
                {
                    options = RegisterRequestParser.ParseBeam(parameters, out var parseWarnings);
                    _handler.SetParseWarnings(parseWarnings);
                }
                catch (RegisterRequestParser.RegisterParseException ex)
                {
                    // Surface validation failures as a soft response so the
                    // server can render them as a warning envelope rather
                    // than a hard command failure.
                    return BeamRegisterErrorResult.FromException(ex);
                }

                _handler.SetOptions(options);

                // Beam extraction iterates over the whole project and may run
                // for several seconds on large models; allocate a generous
                // timeout so paging responses have a chance to flush.
                if (RaiseAndWaitForCompletion(120000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("Beam register extraction timed out.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to extract beam register: {ex.Message}", ex);
            }
        }
    }
}