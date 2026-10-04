using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Datums;

namespace RevitMCPCommandSet.Commands.Datums
{
    public class CreateGridsCommand : JsonParameterCommandBase
    {
        public CreateGridsCommand(UIApplication uiApp)
            : base(new CreateGridsEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "create_grids";
    }
}
