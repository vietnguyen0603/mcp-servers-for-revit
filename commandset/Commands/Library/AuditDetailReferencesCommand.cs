using Autodesk.Revit.UI;
using RevitMCPCommandSet.Services.Library;

namespace RevitMCPCommandSet.Commands.Library
{
    public class AuditDetailReferencesCommand : JsonParameterCommandBase
    {
        public AuditDetailReferencesCommand(UIApplication uiApp)
            : base(new AuditDetailReferencesEventHandler(), uiApp, 120000)
        {
        }

        public override string CommandName => "audit_detail_references";
    }
}
