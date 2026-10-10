using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Modify;

namespace RevitMCPCommandSet.Commands.Modify
{
    public class SetWallTopLevelsCommand : JsonParameterCommandBase
    {
        public SetWallTopLevelsCommand(UIApplication uiApp)
            : base(new SetWallTopLevelsEventHandler(), uiApp, 300000)
        {
        }

        public override string CommandName => "set_wall_top_levels";
    }
}
