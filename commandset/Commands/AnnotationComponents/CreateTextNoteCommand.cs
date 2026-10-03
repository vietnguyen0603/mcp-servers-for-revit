using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class CreateTextNoteCommand : JsonParameterCommandBase
    {
        public CreateTextNoteCommand(UIApplication uiApp)
            : base(new CreateTextNoteEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "create_text_note";
    }
}
