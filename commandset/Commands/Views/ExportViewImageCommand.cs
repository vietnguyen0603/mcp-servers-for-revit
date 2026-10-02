using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class ExportViewImageCommand : JsonParameterCommandBase
    {
        public ExportViewImageCommand(UIApplication uiApp)
            : base(new ExportViewImageEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "export_view_image";
    }
}
