using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class CreateGridDimensionsCommand : JsonParameterCommandBase
    {
        public CreateGridDimensionsCommand(UIApplication uiApp)
            : base(new CreateGridDimensionsEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "create_grid_dimensions";
    }
}
