using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Views;

namespace RevitMCPCommandSet.Commands.Views
{
    public class SetCropRegionCommand : JsonParameterCommandBase
    {
        public SetCropRegionCommand(UIApplication uiApp)
            : base(new SetCropRegionEventHandler(), uiApp, 60000)
        {
        }

        public override string CommandName => "set_crop_region";
    }
}
