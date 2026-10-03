using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class ListSheetsCommand : JsonParameterCommandBase
    {
        public ListSheetsCommand(UIApplication uiApp)
            : base(new ListSheetsEventHandler(), uiApp, 90000)
        {
        }

        public override string CommandName => "list_sheets";
    }
}
