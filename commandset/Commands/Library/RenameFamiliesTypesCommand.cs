using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Library;

namespace RevitMCPCommandSet.Commands.Library
{
    public class RenameFamiliesTypesCommand : JsonParameterCommandBase
    {
        public RenameFamiliesTypesCommand(UIApplication uiApp)
            : base(new RenameFamiliesTypesEventHandler(), uiApp, 300000)
        {
        }

        public override string CommandName => "rename_families_types";
    }
}
