using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class StyleTagFamiliesCommand : JsonParameterCommandBase
    {
        public StyleTagFamiliesCommand(UIApplication uiApp)
            : base(new StyleTagFamiliesEventHandler(), uiApp, 600000)
        {
        }

        public override string CommandName => "style_tag_families";
    }
}
