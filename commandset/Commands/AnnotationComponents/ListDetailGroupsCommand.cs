using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class ListDetailGroupsCommand : JsonParameterCommandBase
    {
        public ListDetailGroupsCommand(UIApplication uiApp)
            : base(new ListDetailGroupsEventHandler(), uiApp)
        {
        }

        public override string CommandName => "list_detail_groups";
    }
}
