using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class CreateViewFilterCommand : JsonParameterCommandBase
    {
        public CreateViewFilterCommand(UIApplication uiApp)
            : base(new CreateViewFilterEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "create_view_filter";
    }
}
