using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class CreateRevisionCloudCommand : JsonParameterCommandBase
    {
        public CreateRevisionCloudCommand(UIApplication uiApp)
            : base(new CreateRevisionCloudEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "create_revision_cloud";
    }
}
