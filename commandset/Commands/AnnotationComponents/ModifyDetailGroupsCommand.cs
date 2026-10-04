using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class ModifyDetailGroupsCommand : JsonParameterCommandBase
    {
        public ModifyDetailGroupsCommand(UIApplication uiApp)
            : base(new ModifyDetailGroupsEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "modify_detail_groups";
    }
}
