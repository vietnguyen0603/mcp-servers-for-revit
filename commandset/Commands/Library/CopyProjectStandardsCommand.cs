using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Library;

namespace RevitMCPCommandSet.Commands.Library
{
    public class CopyProjectStandardsCommand : JsonParameterCommandBase
    {
        public CopyProjectStandardsCommand(UIApplication uiApp)
            : base(new CopyProjectStandardsEventHandler(), uiApp, 600000)
        {
        }

        public override string CommandName => "copy_project_standards";
    }
}
