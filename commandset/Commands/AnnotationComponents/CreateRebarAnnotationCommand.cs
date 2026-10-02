using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class CreateRebarAnnotationCommand : JsonParameterCommandBase
    {
        public CreateRebarAnnotationCommand(UIApplication uiApp)
            : base(new CreateRebarAnnotationEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "create_rebar_annotation";
    }
}
