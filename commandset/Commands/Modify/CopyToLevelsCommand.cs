using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Modify;

namespace RevitMCPCommandSet.Commands.Modify
{
    public class CopyToLevelsCommand : JsonParameterCommandBase
    {
        public CopyToLevelsCommand(UIApplication uiApp)
            : base(new CopyToLevelsEventHandler(), uiApp, 1800000)
        {
        }

        public override string CommandName => "copy_to_levels";
    }
}
