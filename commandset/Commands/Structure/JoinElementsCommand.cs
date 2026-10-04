using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Structure;

namespace RevitMCPCommandSet.Commands.Structure
{
    public class JoinElementsCommand : JsonParameterCommandBase
    {
        // Thousands of joins on a tower model can take several minutes.
        public JoinElementsCommand(UIApplication uiApp)
            : base(new JoinElementsEventHandler(), uiApp, 600000)
        {
        }

        public override string CommandName => "join_elements";
    }
}
