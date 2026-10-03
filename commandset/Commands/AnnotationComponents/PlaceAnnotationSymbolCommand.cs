using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class PlaceAnnotationSymbolCommand : JsonParameterCommandBase
    {
        public PlaceAnnotationSymbolCommand(UIApplication uiApp)
            : base(new PlaceAnnotationSymbolEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "place_annotation_symbol";
    }
}
