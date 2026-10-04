using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Datums;

namespace RevitMCPCommandSet.Commands.Datums
{
    public class SetGridDisplayCommand : JsonParameterCommandBase
    {
        public SetGridDisplayCommand(UIApplication uiApp)
            : base(new SetGridDisplayEventHandler(), uiApp, 180000)
        {
        }

        public override string CommandName => "set_grid_display";
    }
}
