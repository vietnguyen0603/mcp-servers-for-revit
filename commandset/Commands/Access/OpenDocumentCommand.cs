using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Access;

namespace RevitMCPCommandSet.Commands.Access
{
    public class OpenDocumentCommand : JsonParameterCommandBase
    {
        public OpenDocumentCommand(UIApplication uiApp)
            : base(new OpenDocumentEventHandler(), uiApp, 600000)
        {
        }

        public override string CommandName => "open_document";
    }
}
