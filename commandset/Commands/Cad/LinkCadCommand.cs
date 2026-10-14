using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Cad;

namespace RevitMCPCommandSet.Commands.Cad
{
    public class LinkCadCommand : JsonParameterCommandBase
    {
        public LinkCadCommand(UIApplication uiApp)
            : base(new LinkCadEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "link_cad";
    }
}
