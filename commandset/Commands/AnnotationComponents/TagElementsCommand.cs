using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class TagElementsCommand : JsonParameterCommandBase
    {
        public TagElementsCommand(UIApplication uiApp)
            : base(new TagElementsEventHandler(), uiApp, 300000)
        {
        }

        public override string CommandName => "tag_elements";
    }
}
