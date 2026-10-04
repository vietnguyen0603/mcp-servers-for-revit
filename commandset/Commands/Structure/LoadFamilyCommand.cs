using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Structure;

namespace RevitMCPCommandSet.Commands.Structure
{
    public class LoadFamilyCommand : JsonParameterCommandBase
    {
        public LoadFamilyCommand(UIApplication uiApp)
            : base(new LoadFamilyEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "load_family";
    }
}
