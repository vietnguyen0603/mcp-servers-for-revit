using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Library;

namespace RevitMCPCommandSet.Commands.Library
{
    public class GetDraftingViewIndexCommand : JsonParameterCommandBase
    {
        public GetDraftingViewIndexCommand(UIApplication uiApp)
            : base(new GetDraftingViewIndexEventHandler(), uiApp, 180000)
        {
        }

        public override string CommandName => "get_drafting_view_index";
    }
}
