using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Library;

namespace RevitMCPCommandSet.Commands.Library
{
    public class LayoutDetailSheetGridCommand : JsonParameterCommandBase
    {
        public LayoutDetailSheetGridCommand(UIApplication uiApp)
            : base(new LayoutDetailSheetGridEventHandler(), uiApp, 300000)
        {
        }

        public override string CommandName => "layout_detail_sheet_grid";
    }
}
