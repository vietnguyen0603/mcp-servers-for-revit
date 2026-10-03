using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class GetScheduleDataCommand : JsonParameterCommandBase
    {
        public GetScheduleDataCommand(UIApplication uiApp)
            : base(new GetScheduleDataEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "get_schedule_data";
    }
}
