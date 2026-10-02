using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Annotates rebar in a view, either with one independent tag per bar
    ///     set ("Tag") or with a single multi-rebar annotation spanning all
    ///     given bars ("MultiRebar"). Points are model millimetres.
    /// </summary>
    public class CreateRebarAnnotationEventHandler : JsonParameterEventHandler
    {
        /// <summary>Default distance, in paper millimetres, of the tag head from the dimension line.</summary>
        private const double DefaultTagOffsetPaperMm = 10;

        public override string GetName() => "Create Rebar Annotation";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var view = DocumentationUtils.GetElement<View>(doc, DocumentationUtils.ReadId(parameters, "viewId"))
                       ?? uiDoc.ActiveView;
            if (view.IsTemplate || view is ViewSheet || view is ViewSchedule)
                return Fail($"View '{view.Name}' cannot host rebar annotation.");

            var rebars = CollectRebar(doc, view, parameters);
            if (rebars.Count == 0)
                return Fail($"No rebar found in view '{view.Name}'.");

            var mode = (parameters.Value<string>("mode") ?? "Tag").Trim();
            if (string.Equals(mode, "Tag", StringComparison.OrdinalIgnoreCase))
                return TagEach(doc, view, rebars, parameters);
            if (string.Equals(mode, "MultiRebar", StringComparison.OrdinalIgnoreCase))
                return CreateMultiRebar(doc, view, rebars, parameters);
            return Fail($"Unsupported mode '{mode}'. Use Tag or MultiRebar.");
        }

        private AIResult<object> TagEach(Document doc, View view, List<Rebar> rebars, JObject parameters)
        {
            var tagType = DocumentationUtils.GetElement<FamilySymbol>(doc, DocumentationUtils.ReadId(parameters, "tagTypeId"));
            if (DocumentationUtils.ReadId(parameters, "tagTypeId") != null
                && (tagType == null || !DocumentationUtils.IsCategory(tagType, BuiltInCategory.OST_RebarTags)))
                return Fail("'tagTypeId' does not refer to a rebar tag type.");

            var addLeader = parameters.Value<bool?>("addLeader") ?? false;
            var offset = DocumentationUtils.ReadPointMm(parameters["offsetMm"]) ?? XYZ.Zero;
            var items = new JArray(rebars.Select(r => r.Id.GetValue()));

            var results = DocumentationUtils.RunBatch(doc, "MCP: Tag Rebar", items, token =>
            {
                var rebar = (Rebar)doc.GetElement(token.Value<long>().ToRevitElementId());
                var box = rebar.get_BoundingBox(view) ?? rebar.get_BoundingBox(null)
                          ?? throw new InvalidOperationException("Rebar has no extent in this view.");
                var head = (box.Min + box.Max) / 2 + offset;

                if (tagType != null && !tagType.IsActive)
                    tagType.Activate();

                var reference = new Reference(rebar);
                var tag = tagType != null
                    ? IndependentTag.Create(doc, tagType.Id, view.Id, reference, addLeader, TagOrientation.Horizontal, head)
                    : IndependentTag.Create(doc, view.Id, reference, addLeader, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, head);
                return new { rebarId = rebar.Id.GetValue(), tagId = tag.Id.GetValue() };
            });

            return Ok($"Tagged {results.Count(r => r.Value<bool>("success"))} of {results.Count} rebar in '{view.Name}'.",
                DocumentationUtils.Summarize(results));
        }

        private AIResult<object> CreateMultiRebar(Document doc, View view, List<Rebar> rebars, JObject parameters)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(MultiReferenceAnnotationType))
                .Cast<MultiReferenceAnnotationType>()
                .OrderBy(t => t.Id.GetValue())
                .ToList();
            if (types.Count == 0)
                return Fail("No multi-rebar annotation types exist in the project. Load or create one first.");

            var typeId = DocumentationUtils.ReadId(parameters, "multiRebarTypeId");
            var type = typeId != null
                ? types.FirstOrDefault(t => t.Id.GetValue() == typeId)
                : types.First();
            if (type == null)
                return Fail($"multiRebarTypeId {typeId} is not a multi-reference annotation type.");

            var normal = view.ViewDirection.Normalize();
            var direction = DimensionDirection(rebars[0], view, normal);

            var center = Center(rebars, view);
            var offset = DocumentationUtils.ReadPointMm(parameters["offsetMm"]) ?? XYZ.Zero;
            var origin = DocumentationUtils.ReadPointMm(parameters["dimensionLinePoint"]) ?? center + offset;
            var tagOffset = DocumentationUtils.MmToFeet(DefaultTagOffsetPaperMm * Math.Max(1, view.Scale));
            var tagHead = DocumentationUtils.ReadPointMm(parameters["tagHeadPoint"])
                          ?? origin + normal.CrossProduct(direction).Normalize() * tagOffset;

            var options = new MultiReferenceAnnotationOptions(type)
            {
                DimensionLineOrigin = origin,
                DimensionLineDirection = direction,
                DimensionPlaneNormal = normal,
                TagHeadPosition = tagHead,
                TagHasLeader = parameters.Value<bool?>("addLeader") ?? true
            };
            options.SetElementsToDimension(rebars.Select(r => r.Id).ToList());

            if (!MultiReferenceAnnotation.AreElementsValidForMultiReferenceAnnotation(doc, options))
                return Fail("The selected rebar cannot be annotated together with this multi-rebar annotation type in this view.");

            using (var transaction = DocumentationUtils.StartTransaction(doc, "MCP: Create Multi-Rebar Annotation"))
            {
                var annotation = MultiReferenceAnnotation.Create(doc, view.Id, options);
                var status = transaction.Commit();
                if (status != TransactionStatus.Committed)
                    return Fail($"Multi-rebar annotation transaction was not committed ({status}).");

                return Ok($"Created a multi-rebar annotation for {rebars.Count} rebar in '{view.Name}'.", new
                {
                    annotationId = annotation.Id.GetValue(),
                    dimensionId = annotation.DimensionId.GetValue(),
                    tagId = annotation.TagId.GetValue(),
                    typeId = type.Id.GetValue(),
                    rebarIds = rebars.Select(r => r.Id.GetValue()).ToList()
                });
            }
        }

        private static List<Rebar> CollectRebar(Document doc, View view, JObject parameters)
        {
            if (parameters["rebarIds"] is JArray ids && ids.Count > 0)
            {
                var rebars = new List<Rebar>();
                foreach (var id in ids.Select(t => t.Value<long>()).Distinct())
                {
                    rebars.Add(doc.GetElement(id.ToRevitElementId()) as Rebar
                               ?? throw new ArgumentException($"Element {id} is not a rebar (rebar in area/path systems is not supported)."));
                }

                return rebars;
            }

            return new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(Rebar))
                .Cast<Rebar>()
                .OrderBy(r => r.Id.GetValue())
                .ToList();
        }

        /// <summary>
        ///     Direction across the bars in the view plane: perpendicular to the
        ///     first bar's straight segment, or the view's right direction when
        ///     the bars are seen end-on.
        /// </summary>
        private static XYZ DimensionDirection(Rebar rebar, View view, XYZ normal)
        {
            var barDirection = rebar
                .GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0)
                .OfType<Line>()
                .OrderByDescending(l => l.Length)
                .Select(l => l.Direction)
                .FirstOrDefault();

            if (barDirection != null)
            {
                var inPlane = barDirection - normal * barDirection.DotProduct(normal);
                if (inPlane.GetLength() > 1e-6)
                    return normal.CrossProduct(inPlane.Normalize()).Normalize();
            }

            return view.RightDirection.Normalize();
        }

        private static XYZ Center(List<Rebar> rebars, View view)
        {
            XYZ min = null, max = null;
            foreach (var rebar in rebars)
            {
                var box = rebar.get_BoundingBox(view) ?? rebar.get_BoundingBox(null);
                if (box == null)
                    continue;
                min = min == null ? box.Min : new XYZ(Math.Min(min.X, box.Min.X), Math.Min(min.Y, box.Min.Y), Math.Min(min.Z, box.Min.Z));
                max = max == null ? box.Max : new XYZ(Math.Max(max.X, box.Max.X), Math.Max(max.Y, box.Max.Y), Math.Max(max.Z, box.Max.Z));
            }

            return min == null ? view.Origin : (min + max) / 2;
        }
    }
}
