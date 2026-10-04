using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class ListDraftingTypesCommand : JsonParameterCommandBase
    {
        public ListDraftingTypesCommand(UIApplication uiApp)
            : base(new ListDraftingTypesEventHandler(), uiApp)
        {
        }

        public override string CommandName => "list_drafting_types";
    }
}
