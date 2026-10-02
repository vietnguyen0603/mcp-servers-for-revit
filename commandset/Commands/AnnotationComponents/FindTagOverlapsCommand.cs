using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class FindTagOverlapsCommand : JsonParameterCommandBase
    {
        public FindTagOverlapsCommand(UIApplication uiApp)
            : base(new FindTagOverlapsEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "find_tag_overlaps";
    }
}
