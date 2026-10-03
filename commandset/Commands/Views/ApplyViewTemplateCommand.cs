using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class ApplyViewTemplateCommand : JsonParameterCommandBase
    {
        public ApplyViewTemplateCommand(UIApplication uiApp)
            : base(new ApplyViewTemplateEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "apply_view_template";
    }
}
