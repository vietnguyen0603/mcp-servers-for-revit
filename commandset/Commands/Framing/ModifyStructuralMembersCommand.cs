using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Framing;

namespace RevitMCPCommandSet.Commands.Framing
{
    public class ModifyStructuralMembersCommand : JsonParameterCommandBase
    {
        public ModifyStructuralMembersCommand(UIApplication uiApp)
            : base(new ModifyStructuralMembersEventHandler(), uiApp, 300000)
        {
        }

        public override string CommandName => "modify_structural_members";
    }
}
