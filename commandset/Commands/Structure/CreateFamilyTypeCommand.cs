using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Structure;

namespace RevitMCPCommandSet.Commands.Structure
{
    public class CreateFamilyTypeCommand : JsonParameterCommandBase
    {
        public CreateFamilyTypeCommand(UIApplication uiApp)
            : base(new CreateFamilyTypeEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "create_family_type";
    }
}
