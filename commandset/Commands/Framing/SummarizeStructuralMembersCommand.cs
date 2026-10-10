using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Framing;

namespace RevitMCPCommandSet.Commands.Framing
{
    public class SummarizeStructuralMembersCommand : JsonParameterCommandBase
    {
        public SummarizeStructuralMembersCommand(UIApplication uiApp)
            : base(new SummarizeStructuralMembersEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "summarize_structural_members";
    }
}
