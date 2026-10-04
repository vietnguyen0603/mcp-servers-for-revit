using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class ManageAnnotationTypesCommand : JsonParameterCommandBase
    {
        public ManageAnnotationTypesCommand(UIApplication uiApp)
            : base(new ManageAnnotationTypesEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "manage_annotation_types";
    }
}
