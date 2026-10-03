using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class ArrayAnnotationsCommand : JsonParameterCommandBase
    {
        public ArrayAnnotationsCommand(UIApplication uiApp)
            : base(new ArrayAnnotationsEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "array_annotations";
    }
}
