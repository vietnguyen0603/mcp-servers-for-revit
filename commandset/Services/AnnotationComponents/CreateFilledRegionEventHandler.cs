using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Creates filled regions or masking regions from an outer boundary and
    ///     optional holes. With no regions and <c>listTypes</c>, returns the
    ///     filled region types and line styles instead.
    /// </summary>
    public class CreateFilledRegionEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Create Filled Region";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var regions = parameters["regions"] as JArray;
            if ((regions == null || regions.Count == 0) && parameters.Value<bool?>("listTypes") == true)
            {
                return Ok("Listed filled region types and line styles.", new
                {
                    filledRegionTypes = new FilteredElementCollector(doc)
                        .OfClass(typeof(FilledRegionType))
                        .Cast<FilledRegionType>()
                        .OrderBy(t => t.Name)
                        .Select(t => new { id = t.Id.GetValue(), name = t.Name })
                        .ToList(),
                    lineStyles = DetailGeometry.LineStyleNames(doc)
                });
            }

            var view = DetailGeometry.ResolveView(uiDoc, parameters);
            var items = DocumentationUtils.RequireArray(parameters, "regions");

            var results = DocumentationUtils.RunBatch(doc, "MCP: Create Filled Regions", items, token =>
            {
                var item = (JObject)token;
                var loops = new List<CurveLoop>
                {
                    DetailGeometry.Loop(DetailGeometry.ReadPoints(view, item["boundary"], "boundary", 3))
                };
                if (item["holes"] is JArray holes)
                {
                    for (var i = 0; i < holes.Count; i++)
                        loops.Add(DetailGeometry.Loop(DetailGeometry.ReadPoints(view, holes[i], $"holes[{i}]", 3)));
                }

                FilledRegion region;
                if (item.Value<bool?>("masking") == true)
                {
#if REVIT2024_OR_GREATER
                    region = FilledRegion.CreateMaskingRegion(doc, view.Id, loops);
#else
                    throw new NotSupportedException("Masking regions require Revit 2024 or later.");
#endif
                }
                else
                {
                    region = FilledRegion.Create(doc, ResolveType(doc, item), view.Id, loops);
                }

                var lineStyle = DetailGeometry.ResolveLineStyle(doc, item.Value<string>("lineStyle"));
                if (lineStyle != null)
                {
                    if (!FilledRegion.IsValidLineStyleIdForFilledRegion(doc, lineStyle.Id))
                        throw new ArgumentException($"Line style '{lineStyle.Name}' cannot be used for filled region boundaries.");
                    region.SetLineStyleId(lineStyle.Id);
                }

                return new { filledRegionId = region.Id.GetValue(), typeId = region.GetTypeId().GetValue(), isMasking = region.IsMasking };
            });

            return Ok($"Created {results.Count(r => r.Value<bool>("success"))} of {results.Count} regions in '{view.Name}'.",
                DocumentationUtils.Summarize(results));
        }

        private static ElementId ResolveType(Document doc, JObject item)
        {
            var types = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().ToList();

            var typeId = DocumentationUtils.ReadId(item, "filledRegionTypeId");
            if (typeId != null)
                return types.FirstOrDefault(t => t.Id.GetValue() == typeId)?.Id
                       ?? throw new ArgumentException($"filledRegionTypeId {typeId} is not a filled region type.");

            var name = item.Value<string>("filledRegionTypeName");
            if (!string.IsNullOrWhiteSpace(name))
                return types.FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
                       ?? throw new ArgumentException(
                           $"Filled region type '{name}' not found. Available: {string.Join(", ", types.Select(t => t.Name).OrderBy(n => n))}.");

            var defaultId = doc.GetDefaultElementTypeId(ElementTypeGroup.FilledRegionType);
            if (defaultId != ElementId.InvalidElementId)
                return defaultId;
            return types.OrderBy(t => t.Id.GetValue()).FirstOrDefault()?.Id
                   ?? throw new InvalidOperationException("No filled region types exist in the project.");
        }
    }
}
