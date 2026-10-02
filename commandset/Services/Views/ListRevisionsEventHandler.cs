using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Lists revisions in sequence order with the sheets each appears on,
    ///     plus the revision numbering sequences (Revit 2022+). Read-only.
    /// </summary>
    public class ListRevisionsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "List Revisions";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;

            var sheetsByRevision = new Dictionary<long, List<string>>();
            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsTemplate)
                .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase);
            foreach (var sheet in sheets)
            {
                foreach (var revisionId in sheet.GetAllRevisionIds())
                {
                    var key = revisionId.GetValue();
                    if (!sheetsByRevision.TryGetValue(key, out var list))
                        sheetsByRevision[key] = list = new List<string>();
                    list.Add(sheet.SheetNumber);
                }
            }

            var revisions = Revision.GetAllRevisionIds(doc)
                .Select(id => doc.GetElement(id) as Revision)
                .Where(r => r != null)
                .Select(r => new
                {
                    id = r.Id.GetValue(),
                    sequenceNumber = r.SequenceNumber,
                    revisionNumber = r.RevisionNumber,
                    date = r.RevisionDate,
                    description = r.Description,
                    issued = r.Issued,
                    issuedBy = r.IssuedBy,
                    issuedTo = r.IssuedTo,
                    visibility = r.Visibility.ToString(),
#if REVIT2022_OR_GREATER
                    numberingSequence = (doc.GetElement(r.RevisionNumberingSequenceId) as RevisionNumberingSequence)?.Name,
#else
                    numberType = r.NumberType.ToString(),
#endif
                    sheets = sheetsByRevision.TryGetValue(r.Id.GetValue(), out var list) ? list : new List<string>()
                })
                .ToList();

#if REVIT2022_OR_GREATER
            object numberingSequences = RevisionNumberingSequence.GetAllRevisionNumberingSequences(doc)
                .Select(id => doc.GetElement(id) as RevisionNumberingSequence)
                .Where(s => s != null)
                .Select(s => new { id = s.Id.GetValue(), name = s.Name, numberType = s.NumberType.ToString() })
                .ToList();
#else
            object numberingSequences = null;
#endif

            return Ok($"Found {revisions.Count} revisions.", new { revisions, numberingSequences });
        }
    }
}
