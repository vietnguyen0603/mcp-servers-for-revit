using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Architecture;

namespace RevitMCPCommandSet.Commands.Architecture
{
    public class CreateOpeningsCommand : JsonParameterCommandBase
    {
        public CreateOpeningsCommand(UIApplication uiApp)
            : base(new CreateOpeningsEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "create_openings";
    }
}
