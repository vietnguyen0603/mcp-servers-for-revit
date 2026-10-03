using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class CreateScheduleCommand : JsonParameterCommandBase
    {
        public CreateScheduleCommand(UIApplication uiApp)
            : base(new CreateScheduleEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "create_schedule";
    }
}
