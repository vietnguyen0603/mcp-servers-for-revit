using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Foundations;

namespace RevitMCPCommandSet.Commands.Foundations
{
    public class CreateFoundationsCommand : JsonParameterCommandBase
    {
        public CreateFoundationsCommand(UIApplication uiApp)
            : base(new CreateFoundationsEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "create_foundations";
    }
}
