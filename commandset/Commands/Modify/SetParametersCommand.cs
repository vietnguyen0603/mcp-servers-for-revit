using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Modify;

namespace RevitMCPCommandSet.Commands.Modify
{
    public class SetParametersCommand : JsonParameterCommandBase
    {
        public SetParametersCommand(UIApplication uiApp)
            : base(new SetParametersEventHandler(), uiApp, 600000)
        {
        }

        public override string CommandName => "set_parameters";
    }
}
