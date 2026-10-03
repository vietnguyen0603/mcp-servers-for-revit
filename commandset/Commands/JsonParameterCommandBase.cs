using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands
{
    /// <summary>
    ///     Command that forwards its JSON parameters to a
    ///     <see cref="JsonParameterEventHandler" /> and returns the handler result.
    ///     Validation and parsing happen in the handler, on the Revit thread.
    /// </summary>
    public abstract class JsonParameterCommandBase : ExternalEventCommandBase
    {
        private readonly int _timeoutMilliseconds;

        protected JsonParameterCommandBase(JsonParameterEventHandler handler, UIApplication uiApp,
            int timeoutMilliseconds = 60000)
            : base(handler, uiApp)
        {
            _timeoutMilliseconds = timeoutMilliseconds;
        }

        private JsonParameterEventHandler JsonHandler => (JsonParameterEventHandler)Handler;

        public override object Execute(JObject parameters, string requestId)
        {
            JsonHandler.SetParameters(parameters);

            if (RaiseAndWaitForCompletion(_timeoutMilliseconds))
                return JsonHandler.Result;

            throw new TimeoutException($"{CommandName} timed out after {_timeoutMilliseconds / 1000} seconds.");
        }
    }
}
