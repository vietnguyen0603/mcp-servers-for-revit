using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Framing;

namespace RevitMCPCommandSet.Commands.Framing
{
    public class CreateBeamsCommand : JsonParameterCommandBase
    {
        public CreateBeamsCommand(UIApplication uiApp)
            : base(new CreateBeamsEventHandler(), uiApp, 600000)
        {
        }

        public override string CommandName => "create_beams";
    }
}
