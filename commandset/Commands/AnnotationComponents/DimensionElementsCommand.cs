using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class DimensionElementsCommand : JsonParameterCommandBase
    {
        public DimensionElementsCommand(UIApplication uiApp)
            : base(new DimensionElementsEventHandler(), uiApp, 300000)
        {
        }

        public override string CommandName => "dimension_elements";
    }
}
