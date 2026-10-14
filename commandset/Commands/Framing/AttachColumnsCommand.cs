using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Framing;

namespace RevitMCPCommandSet.Commands.Framing
{
    public class AttachColumnsCommand : JsonParameterCommandBase
    {
        public AttachColumnsCommand(UIApplication uiApp)
            : base(new AttachColumnsEventHandler(), uiApp, 300000)
        {
        }

        public override string CommandName => "attach_columns";
    }
}
