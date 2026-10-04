using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Modify;

namespace RevitMCPCommandSet.Commands.Modify
{
    public class SetWorksetCommand : JsonParameterCommandBase
    {
        public SetWorksetCommand(UIApplication uiApp)
            : base(new SetWorksetEventHandler(), uiApp, 600000)
        {
        }

        public override string CommandName => "set_workset";
    }
}
