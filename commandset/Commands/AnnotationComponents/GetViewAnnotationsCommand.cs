using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class GetViewAnnotationsCommand : JsonParameterCommandBase
    {
        public GetViewAnnotationsCommand(UIApplication uiApp)
            : base(new GetViewAnnotationsEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "get_view_annotations";
    }
}
