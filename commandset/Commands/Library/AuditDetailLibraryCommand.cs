using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Library;

namespace RevitMCPCommandSet.Commands.Library
{
    public class AuditDetailLibraryCommand : JsonParameterCommandBase
    {
        public AuditDetailLibraryCommand(UIApplication uiApp)
            : base(new AuditDetailLibraryEventHandler(), uiApp, 180000)
        {
        }

        public override string CommandName => "audit_detail_library";
    }
}
