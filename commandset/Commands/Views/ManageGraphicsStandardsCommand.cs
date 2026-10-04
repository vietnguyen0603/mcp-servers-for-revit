using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class ManageGraphicsStandardsCommand : JsonParameterCommandBase
    {
        public ManageGraphicsStandardsCommand(UIApplication uiApp)
            : base(new ManageGraphicsStandardsEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "manage_graphics_standards";
    }
}
