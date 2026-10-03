using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class PlaceViewportCommand : JsonParameterCommandBase
    {
        public PlaceViewportCommand(UIApplication uiApp)
            : base(new PlaceViewportEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "place_viewport";
    }
}
