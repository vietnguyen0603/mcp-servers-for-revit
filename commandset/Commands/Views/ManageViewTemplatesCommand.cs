using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class ManageViewTemplatesCommand : JsonParameterCommandBase
    {
        public ManageViewTemplatesCommand(UIApplication uiApp)
            : base(new ManageViewTemplatesEventHandler(), uiApp, 300000)
        {
        }

        public override string CommandName => "manage_view_templates";
    }
}
