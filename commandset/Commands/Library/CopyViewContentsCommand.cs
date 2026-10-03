using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Library;

namespace RevitMCPCommandSet.Commands.Library
{
    public class CopyViewContentsCommand : JsonParameterCommandBase
    {
        public CopyViewContentsCommand(UIApplication uiApp)
            : base(new CopyViewContentsEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "copy_view_contents";
    }
}
