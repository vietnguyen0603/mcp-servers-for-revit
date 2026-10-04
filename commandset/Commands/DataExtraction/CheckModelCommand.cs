using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.DataExtraction;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    public class CheckModelCommand : JsonParameterCommandBase
    {
        // Solid booleans over thousands of elements can take several minutes.
        public CheckModelCommand(UIApplication uiApp)
            : base(new CheckModelEventHandler(), uiApp, 600000)
        {
        }

        public override string CommandName => "check_model";
    }
}
