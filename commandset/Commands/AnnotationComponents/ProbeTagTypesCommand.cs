using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.AnnotationComponents;

namespace RevitMCPCommandSet.Commands.AnnotationComponents
{
    public class ProbeTagTypesCommand : JsonParameterCommandBase
    {
        public ProbeTagTypesCommand(UIApplication uiApp)
            : base(new ProbeTagTypesEventHandler(), uiApp, 180000)
        {
        }

        public override string CommandName => "probe_tag_types";
    }
}
