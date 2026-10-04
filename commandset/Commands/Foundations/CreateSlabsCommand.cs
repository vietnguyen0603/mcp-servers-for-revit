using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Foundations;

namespace RevitMCPCommandSet.Commands.Foundations
{
    public class CreateSlabsCommand : JsonParameterCommandBase
    {
        public CreateSlabsCommand(UIApplication uiApp)
            : base(new CreateSlabsEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "create_slabs";
    }
}
