using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Modify;

namespace RevitMCPCommandSet.Commands.Modify
{
    public class DeleteElementsCommand : JsonParameterCommandBase
    {
        public DeleteElementsCommand(UIApplication uiApp)
            : base(new DeleteElementsEventHandler(), uiApp, 600000)
        {
        }

        public override string CommandName => "delete_elements";
    }
}
