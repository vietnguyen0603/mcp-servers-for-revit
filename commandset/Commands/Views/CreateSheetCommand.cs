using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class CreateSheetCommand : JsonParameterCommandBase
    {
        public CreateSheetCommand(UIApplication uiApp)
            : base(new CreateSheetEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "create_sheet";
    }
}
