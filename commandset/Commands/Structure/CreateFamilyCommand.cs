using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Structure;

namespace RevitMCPCommandSet.Commands.Structure
{
    public class CreateFamilyCommand : JsonParameterCommandBase
    {
        public CreateFamilyCommand(UIApplication uiApp)
            : base(new CreateFamilyEventHandler(), uiApp, 300000)
        {
        }

        public override string CommandName => "create_family";
    }
}
