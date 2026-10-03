using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class AlignViewportsCommand : JsonParameterCommandBase
    {
        public AlignViewportsCommand(UIApplication uiApp)
            : base(new AlignViewportsEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "align_viewports";
    }
}
