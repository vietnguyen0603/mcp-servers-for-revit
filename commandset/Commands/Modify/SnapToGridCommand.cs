using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Modify;

namespace RevitMCPCommandSet.Commands.Modify
{
    public class SnapToGridCommand : JsonParameterCommandBase
    {
        public SnapToGridCommand(UIApplication uiApp)
            : base(new SnapToGridEventHandler(), uiApp, 600000)
        {
        }

        public override string CommandName => "snap_to_grid";
    }
}
