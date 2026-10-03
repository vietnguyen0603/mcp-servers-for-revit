using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class ListViewsCommand : JsonParameterCommandBase
    {
        public ListViewsCommand(UIApplication uiApp)
            : base(new ListViewsEventHandler(), uiApp, 30000)
        {
        }

        public override string CommandName => "list_views";
    }
}
