using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class CreateDetailLinesCommand : JsonParameterCommandBase
    {
        public CreateDetailLinesCommand(UIApplication uiApp)
            : base(new CreateDetailLinesEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "create_detail_lines";
    }
}
