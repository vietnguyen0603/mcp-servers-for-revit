using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Datums;

namespace RevitMCPCommandSet.Commands.Datums
{
    public class CreateOffsetLevelsCommand : JsonParameterCommandBase
    {
        public CreateOffsetLevelsCommand(UIApplication uiApp)
            : base(new CreateOffsetLevelsEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "create_offset_levels";
    }
}
