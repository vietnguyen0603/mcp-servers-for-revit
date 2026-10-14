using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Framing;

namespace RevitMCPCommandSet.Commands.Framing
{
    public class CreateTrussesCommand : JsonParameterCommandBase
    {
        public CreateTrussesCommand(UIApplication uiApp)
            : base(new CreateTrussesEventHandler(), uiApp, 600000)
        {
        }

        public override string CommandName => "create_trusses";
    }
}
