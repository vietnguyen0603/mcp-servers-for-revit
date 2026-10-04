using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Datums;

namespace RevitMCPCommandSet.Commands.Datums
{
    public class ModifyLevelsCommand : JsonParameterCommandBase
    {
        public ModifyLevelsCommand(UIApplication uiApp)
            : base(new ModifyLevelsEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "modify_levels";
    }
}
