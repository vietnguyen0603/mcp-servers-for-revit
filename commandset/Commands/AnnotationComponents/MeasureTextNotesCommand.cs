using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class MeasureTextNotesCommand : JsonParameterCommandBase
    {
        public MeasureTextNotesCommand(UIApplication uiApp)
            : base(new MeasureTextNotesEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "measure_text_notes";
    }
}
