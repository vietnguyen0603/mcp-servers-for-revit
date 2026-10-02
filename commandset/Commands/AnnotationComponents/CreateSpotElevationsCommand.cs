using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class CreateSpotElevationsCommand : JsonParameterCommandBase
    {
        public CreateSpotElevationsCommand(UIApplication uiApp)
            : base(new CreateSpotElevationsEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "create_spot_elevations";
    }
}
