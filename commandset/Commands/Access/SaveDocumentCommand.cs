using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Access;

namespace RevitMCPCommandSet.Commands.Access
{
    public class SaveDocumentCommand : JsonParameterCommandBase
    {
        public SaveDocumentCommand(UIApplication uiApp)
            : base(new SaveDocumentEventHandler(), uiApp, 600000)
        {
        }

        public override string CommandName => "save_document";
    }
}
