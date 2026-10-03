using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class PlaceDetailComponentCommand : JsonParameterCommandBase
    {
        public PlaceDetailComponentCommand(UIApplication uiApp)
            : base(new PlaceDetailComponentEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "place_detail_component";
    }
}
