using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Structure;

namespace RevitMCPCommandSet.Commands.Structure
{
    public class CopyFamiliesCommand : JsonParameterCommandBase
    {
        public CopyFamiliesCommand(UIApplication uiApp)
            : base(new CopyFamiliesEventHandler(), uiApp, 600000)
        {
        }

        public override string CommandName => "copy_families";
    }
}
