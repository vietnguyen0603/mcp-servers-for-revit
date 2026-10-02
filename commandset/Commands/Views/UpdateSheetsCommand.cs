using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class UpdateSheetsCommand : JsonParameterCommandBase
    {
        public UpdateSheetsCommand(UIApplication uiApp)
            : base(new UpdateSheetsEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "update_sheets";
    }
}
