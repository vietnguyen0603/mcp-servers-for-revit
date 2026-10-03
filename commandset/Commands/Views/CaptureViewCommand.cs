using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class CaptureViewCommand : JsonParameterCommandBase
    {
        public CaptureViewCommand(UIApplication uiApp)
            : base(new CaptureViewEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "capture_view";
    }
}
