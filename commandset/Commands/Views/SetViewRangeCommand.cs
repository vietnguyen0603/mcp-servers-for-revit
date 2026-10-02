using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class SetViewRangeCommand : JsonParameterCommandBase
    {
        public SetViewRangeCommand(UIApplication uiApp)
            : base(new SetViewRangeEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "set_view_range";
    }
}
