using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Library;

namespace RevitMCPCommandSet.Commands.Library
{
    public class ExportProjectStyleCommand : JsonParameterCommandBase
    {
        public ExportProjectStyleCommand(UIApplication uiApp)
            : base(new ExportProjectStyleEventHandler(), uiApp, 600000)
        {
        }

        public override string CommandName => "export_project_style";
    }
}
