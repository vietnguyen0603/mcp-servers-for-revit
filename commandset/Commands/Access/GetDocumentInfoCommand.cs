using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Access;

namespace RevitMCPCommandSet.Commands.Access
{
    public class GetDocumentInfoCommand : JsonParameterCommandBase
    {
        public GetDocumentInfoCommand(UIApplication uiApp)
            : base(new GetDocumentInfoEventHandler(), uiApp, 30000)
        {
        }

        public override string CommandName => "get_document_info";
    }
}
