using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Cad;

namespace RevitMCPCommandSet.Commands.Cad
{
    public class TraceCadImportCommand : JsonParameterCommandBase
    {
        public TraceCadImportCommand(UIApplication uiApp)
            : base(new TraceCadImportEventHandler(), uiApp, 300000)
        {
        }

        public override string CommandName => "trace_cad_import";
    }
}
