using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Library
{
    /// <summary>
    ///     Copies view-specific elements (detail lines, regions, components,
    ///     text, dimensions, detail groups...) from a source view into a target
    ///     view of the active document. The source view may live in another
    ///     open document, which enables library-to-project transfer. Types that
    ///     already exist in the destination are reused.
    /// </summary>
    public class CopyViewContentsEventHandler : JsonParameterEventHandler
    {
        private const int MaxReturnedIds = 2000;

        public override string GetName() => "Copy View Contents";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var sourceDoc = ViewCopyUtils.FindDocument(uiDoc.Application.Application, doc,
                parameters.Value<string>("sourceDocumentTitle"));
            var crossDocument = !sourceDoc.Equals(doc);

            var source = DocumentationUtils.GetElement<View>(sourceDoc, DocumentationUtils.ReadId(parameters, "sourceViewId"))
                         ?? throw new ArgumentException($"'sourceViewId' is not a view in '{sourceDoc.Title}'.");
            var target = DocumentationUtils.GetElement<View>(doc, DocumentationUtils.ReadId(parameters, "targetViewId"))
                         ?? throw new ArgumentException($"'targetViewId' is not a view in '{doc.Title}'.");
            if (source.IsTemplate || target.IsTemplate)
                throw new ArgumentException("View templates have no content to copy.");
            if (!crossDocument && source.Id == target.Id)
                throw new ArgumentException("Source and target view must differ.");

            var skipped = new List<object>();
            List<ElementId> ids;
            if (parameters["elementIds"] is JArray requested && requested.Count > 0)
            {
                ids = new List<ElementId>();
                foreach (var token in requested)
                {
                    var id = token.Value<long>();
                    var element = sourceDoc.GetElement(id.ToRevitElementId());
                    if (element == null)
                        skipped.Add(new { id, reason = "not found" });
                    else if (element.OwnerViewId != source.Id)
                        skipped.Add(new { id, reason = "not owned by the source view" });
                    else
                        ids.Add(element.Id);
                }
            }
            else
            {
                ids = ViewCopyUtils.CopyableElements(sourceDoc, source, crossDocument);
            }

            if (ids.Count == 0)
                return Fail($"Nothing to copy from '{source.Name}'.");

            var offset = DocumentationUtils.ReadPointMm(parameters["offset"]);
            var transform = offset == null
                ? Transform.Identity
                : Transform.CreateTranslation(target.RightDirection * offset.X + target.UpDirection * offset.Y);

            ICollection<ElementId> copied;
            using (var transaction = DocumentationUtils.StartTransaction(doc, "MCP: Copy View Contents"))
            {
                copied = ElementTransformUtils.CopyElements(source, ids, target, transform,
                    ViewCopyUtils.DestinationTypeOptions());
                var status = transaction.Commit();
                if (status != TransactionStatus.Committed)
                    return Fail($"Copy was not committed ({status}).");
            }

            var newIds = copied.Select(id => id.GetValue()).OrderBy(id => id).ToList();
            return Ok($"Copied {ids.Count} elements from '{source.Name}' to '{target.Name}' ({newIds.Count} new elements).", new
            {
                sourceDocument = sourceDoc.Title,
                sourceViewId = source.Id.GetValue(),
                sourceViewName = source.Name,
                targetViewId = target.Id.GetValue(),
                targetViewName = target.Name,
                sourceElementCount = ids.Count,
                newElementCount = newIds.Count,
                newElementIds = newIds.Take(MaxReturnedIds).ToList(),
                newElementIdsTruncated = newIds.Count > MaxReturnedIds,
                skipped
            });
        }
    }
}
