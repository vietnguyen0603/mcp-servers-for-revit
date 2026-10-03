using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class ModifyAnnotationsCommand : JsonParameterCommandBase
    {
        public ModifyAnnotationsCommand(UIApplication uiApp)
            : base(new ModifyAnnotationsEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "modify_annotations";
    }
}
