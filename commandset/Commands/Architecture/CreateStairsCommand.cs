using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Architecture;

namespace RevitMCPCommandSet.Commands.Architecture
{
    public class CreateStairsCommand : JsonParameterCommandBase
    {
        public CreateStairsCommand(UIApplication uiApp)
            : base(new CreateStairsEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "create_stairs";
    }
}
