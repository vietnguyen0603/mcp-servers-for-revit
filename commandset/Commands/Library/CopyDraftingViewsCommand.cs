using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Library;

namespace RevitMCPCommandSet.Commands.Library
{
    public class CopyDraftingViewsCommand : JsonParameterCommandBase
    {
        public CopyDraftingViewsCommand(UIApplication uiApp)
            : base(new CopyDraftingViewsEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "copy_drafting_views";
    }
}
