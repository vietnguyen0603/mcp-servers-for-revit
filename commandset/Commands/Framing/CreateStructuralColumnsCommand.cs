using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Framing;

namespace RevitMCPCommandSet.Commands.Framing
{
    public class CreateStructuralColumnsCommand : JsonParameterCommandBase
    {
        public CreateStructuralColumnsCommand(UIApplication uiApp)
            : base(new CreateStructuralColumnsEventHandler(), uiApp, 600000)
        {
        }

        public override string CommandName => "create_structural_columns";
    }
}
