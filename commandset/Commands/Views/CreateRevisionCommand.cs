using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class CreateRevisionCommand : JsonParameterCommandBase
    {
        public CreateRevisionCommand(UIApplication uiApp)
            : base(new CreateRevisionEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "create_revision";
    }
}
