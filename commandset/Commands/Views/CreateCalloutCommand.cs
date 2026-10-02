using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class CreateCalloutCommand : JsonParameterCommandBase
    {
        public CreateCalloutCommand(UIApplication uiApp)
            : base(new CreateCalloutEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "create_callout";
    }
}
