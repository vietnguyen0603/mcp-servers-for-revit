using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class UpdateViewportsCommand : JsonParameterCommandBase
    {
        public UpdateViewportsCommand(UIApplication uiApp)
            : base(new UpdateViewportsEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "update_viewports";
    }
}
