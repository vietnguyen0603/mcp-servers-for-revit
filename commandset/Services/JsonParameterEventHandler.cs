using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    /// <summary>
    ///     Base external-event handler for commands that take their raw JSON
    ///     parameters and return an <see cref="AIResult{T}" />. Subclasses only
    ///     implement <see cref="Run" />, which executes on the Revit main thread.
    /// </summary>
    public abstract class JsonParameterEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        protected JObject Parameters { get; private set; } = new JObject();

        public AIResult<object> Result { get; private set; }

        public void SetParameters(JObject parameters)
        {
            Parameters = parameters ?? new JObject();
            Result = null;
            _resetEvent.Reset();
        }

        public void Execute(UIApplication app)
        {
            try
            {
                Result = RunWithApplication(app, Parameters);
            }
            catch (Exception ex)
            {
                Result = Fail(ex.Message);
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        /// <summary>
        ///     Waits for <see cref="Execute" />. The event is reset by
        ///     <see cref="SetParameters" /> so a fast completion is not lost.
        /// </summary>
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public abstract string GetName();

        protected abstract AIResult<object> Run(UIDocument uiDoc, JObject parameters);

        /// <summary>
        ///     Entry point on the Revit main thread. The default requires an active
        ///     document and calls <see cref="Run" />; commands that work without one
        ///     (e.g. opening a document) override this.
        /// </summary>
        protected virtual AIResult<object> RunWithApplication(UIApplication app, JObject parameters)
        {
            var uiDoc = app.ActiveUIDocument;
            return uiDoc?.Document == null
                ? Fail("No active Revit document.")
                : Run(uiDoc, parameters);
        }

        protected static AIResult<object> Ok(string message, object response)
        {
            return new AIResult<object> { Success = true, Message = message, Response = response };
        }

        protected static AIResult<object> Fail(string message)
        {
            return new AIResult<object> { Success = false, Message = message };
        }
    }
}
