using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Cad;

namespace RevitMCPCommandSet.Commands.Cad
{
    public class GetCadGeometryCommand : JsonParameterCommandBase
    {
        public GetCadGeometryCommand(UIApplication uiApp)
            : base(new GetCadGeometryEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "get_cad_geometry";
    }
}
