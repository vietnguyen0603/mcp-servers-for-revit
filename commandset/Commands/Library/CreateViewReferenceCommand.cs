using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Library;

namespace RevitMCPCommandSet.Commands.Library
{
    public class CreateViewReferenceCommand : JsonParameterCommandBase
    {
        public CreateViewReferenceCommand(UIApplication uiApp)
            : base(new CreateViewReferenceEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "create_view_reference";
    }
}
