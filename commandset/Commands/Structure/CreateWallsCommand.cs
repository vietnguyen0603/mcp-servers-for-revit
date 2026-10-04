using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Structure;

namespace RevitMCPCommandSet.Commands.Structure
{
    public class CreateWallsCommand : JsonParameterCommandBase
    {
        public CreateWallsCommand(UIApplication uiApp)
            : base(new CreateWallsEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "create_walls";
    }
}
