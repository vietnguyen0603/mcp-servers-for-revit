using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Library;

namespace RevitMCPCommandSet.Commands.Library
{
    public class GetDraftingStandardsCommand : JsonParameterCommandBase
    {
        public GetDraftingStandardsCommand(UIApplication uiApp)
            : base(new GetDraftingStandardsEventHandler(), uiApp, 180000)
        {
        }

        public override string CommandName => "get_drafting_standards";
    }
}
