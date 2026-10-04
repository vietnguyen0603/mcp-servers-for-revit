using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Modify;

namespace RevitMCPCommandSet.Commands.Modify
{
    public class CreateProjectParameterCommand : JsonParameterCommandBase
    {
        public CreateProjectParameterCommand(UIApplication uiApp)
            : base(new CreateProjectParameterEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "create_project_parameter";
    }
}
