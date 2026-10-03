using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Library;

namespace RevitMCPCommandSet.Commands.Library
{
    public class SyncDetailReferencesCommand : JsonParameterCommandBase
    {
        public SyncDetailReferencesCommand(UIApplication uiApp)
            : base(new SyncDetailReferencesEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "sync_detail_references";
    }
}
