using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Cad;

namespace RevitMCPCommandSet.Commands.Cad
{
    public class PurgeCadLeftoversCommand : JsonParameterCommandBase
    {
        public PurgeCadLeftoversCommand(UIApplication uiApp)
            : base(new PurgeCadLeftoversEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "purge_cad_leftovers";
    }
}
