using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class OverrideGraphicsCommand : JsonParameterCommandBase
    {
        public OverrideGraphicsCommand(UIApplication uiApp)
            : base(new OverrideGraphicsEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "override_graphics";
    }
}
