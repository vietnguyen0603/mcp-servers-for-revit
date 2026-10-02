using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class ExportSheetsCommand : JsonParameterCommandBase
    {
        public ExportSheetsCommand(UIApplication uiApp)
            : base(new ExportSheetsEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "export_sheets";
    }
}
