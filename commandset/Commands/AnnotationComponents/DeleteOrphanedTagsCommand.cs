using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class DeleteOrphanedTagsCommand : JsonParameterCommandBase
    {
        public DeleteOrphanedTagsCommand(UIApplication uiApp)
            : base(new DeleteOrphanedTagsEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "delete_orphaned_tags";
    }
}
