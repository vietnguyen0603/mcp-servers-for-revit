using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class CreateFilledRegionCommand : JsonParameterCommandBase
    {
        public CreateFilledRegionCommand(UIApplication uiApp)
            : base(new CreateFilledRegionEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "create_filled_region";
    }
}
