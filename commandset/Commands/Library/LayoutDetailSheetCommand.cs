using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Library;

namespace RevitMCPCommandSet.Commands.Library
{
    public class LayoutDetailSheetCommand : JsonParameterCommandBase
    {
        public LayoutDetailSheetCommand(UIApplication uiApp)
            : base(new LayoutDetailSheetEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "layout_detail_sheet";
    }
}
