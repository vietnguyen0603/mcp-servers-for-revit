using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class CreateViewCommand : JsonParameterCommandBase
    {
        public CreateViewCommand(UIApplication uiApp)
            : base(new CreateViewEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "create_view";
    }
}
