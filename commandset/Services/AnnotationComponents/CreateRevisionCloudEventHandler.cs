using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Creates revision clouds around rectangles or polygons in a view or
    ///     on a sheet, assigned to the given revision or the latest one.
    /// </summary>
    public class CreateRevisionCloudEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Create Revision Cloud";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var view = DetailGeometry.ResolveView(uiDoc, parameters);
            var items = DocumentationUtils.RequireArray(parameters, "clouds");

            var revisionId = DocumentationUtils.ReadId(parameters, "revisionId");
            Revision revision;
            if (revisionId != null)
            {
                revision = DocumentationUtils.GetElement<Revision>(doc, revisionId);
                if (revision == null)
                    return Fail($"revisionId {revisionId} is not a revision.");
            }
            else
            {
                var all = Revision.GetAllRevisionIds(doc);
                if (all.Count == 0)
                    return Fail("The project has no revisions. Create one first (create_revision).");
                revision = (Revision)doc.GetElement(all[all.Count - 1]);
            }

            if (revision.Issued)
                return Fail($"Revision {revision.SequenceNumber} '{revision.Description}' is issued; clouds cannot be added to it.");

            var results = DocumentationUtils.RunBatch(doc, "MCP: Create Revision Clouds", items, token =>
            {
                var item = (JObject)token;
                List<XYZ> points;
                if (item["rectangle"] is JObject rectangle)
                {
                    points = DetailGeometry.Rectangle(view,
                        DetailGeometry.ReadPoint(view, rectangle["min"], "rectangle.min"),
                        DetailGeometry.ReadPoint(view, rectangle["max"], "rectangle.max"));
                }
                else
                {
                    points = DetailGeometry.ReadPoints(view, item["points"], "points", 3);
                    if (points.Count > 1 && points[0].IsAlmostEqualTo(points[points.Count - 1]))
                        points.RemoveAt(points.Count - 1);
                }

                var cloud = RevisionCloud.Create(doc, view, revision.Id, DetailGeometry.Polyline(points, true));
                return new { revisionCloudId = cloud.Id.GetValue() };
            });

            return Ok($"Created {results.Count(r => r.Value<bool>("success"))} of {results.Count} revision clouds in '{view.Name}'.", new
            {
                revisionId = revision.Id.GetValue(),
                revisionSequence = revision.SequenceNumber,
                summary = DocumentationUtils.Summarize(results)
            });
        }
    }
}
