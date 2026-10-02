using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Creates revisions. The issued flag is applied last because issued
    ///     revisions lock their other fields. Numbering sequences by name
    ///     require Revit 2022 or later.
    /// </summary>
    public class CreateRevisionEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Create Revision";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "revisions");
            var results = DocumentationUtils.RunBatch(doc, "MCP: Create Revisions", items, item => Create(doc, (JObject)item));
            return Ok($"Created {results.Count(r => r.Value<bool>("success"))} of {results.Count} revisions.",
                DocumentationUtils.Summarize(results));
        }

        private static object Create(Document doc, JObject item)
        {
            var sequenceName = item.Value<string>("numberingSequenceName");
#if REVIT2022_OR_GREATER
            ElementId sequenceId = null;
            if (!string.IsNullOrWhiteSpace(sequenceName))
            {
                sequenceId = RevisionNumberingSequence.GetAllRevisionNumberingSequences(doc)
                                 .Select(id => doc.GetElement(id) as RevisionNumberingSequence)
                                 .FirstOrDefault(s => s != null && string.Equals(s.Name, sequenceName.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
                             ?? throw new ArgumentException($"Revision numbering sequence '{sequenceName}' not found.");
            }
#else
            if (!string.IsNullOrWhiteSpace(sequenceName))
                throw new NotSupportedException("numberingSequenceName requires Revit 2022 or later.");
#endif

            var revision = Revision.Create(doc);

            var description = item.Value<string>("description");
            if (description != null)
                revision.Description = description;
            var date = item.Value<string>("date");
            if (date != null)
                revision.RevisionDate = date;
            var issuedBy = item.Value<string>("issuedBy");
            if (issuedBy != null)
                revision.IssuedBy = issuedBy;
            var issuedTo = item.Value<string>("issuedTo");
            if (issuedTo != null)
                revision.IssuedTo = issuedTo;

            var visibility = item.Value<string>("visibility");
            if (!string.IsNullOrWhiteSpace(visibility))
                revision.Visibility = DocumentationUtils.ParseEnum(visibility, RevisionVisibility.CloudAndTagVisible);

#if REVIT2022_OR_GREATER
            if (sequenceId != null)
                revision.RevisionNumberingSequenceId = sequenceId;
#endif

            if (item.Value<bool?>("issued") == true)
                revision.Issued = true;

            return new
            {
                revisionId = revision.Id.GetValue(),
                sequenceNumber = revision.SequenceNumber,
                revisionNumber = revision.RevisionNumber,
                description = revision.Description,
                issued = revision.Issued
            };
        }
    }
}
