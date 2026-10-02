using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class ListRevisionsCommand : JsonParameterCommandBase
    {
        public ListRevisionsCommand(UIApplication uiApp)
            : base(new ListRevisionsEventHandler(), uiApp, 30000)
        {
        }

        public override string CommandName => "list_revisions";
    }
}
