using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class DuplicateViewCommand : JsonParameterCommandBase
    {
        public DuplicateViewCommand(UIApplication uiApp)
            : base(new DuplicateViewEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "duplicate_view";
    }
}
