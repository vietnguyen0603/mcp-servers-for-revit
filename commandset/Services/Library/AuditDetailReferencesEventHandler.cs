using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Library
{
    /// <summary>
    ///     Audits manual detail-reference bubbles (view-specific family
    ///     instances with "Detail Number" / "Sheet Number" text parameters)
    ///     against the viewports actually placed on sheets, and lists live View
    ///     References (OST_ReferenceViewer) with their target views. Read-only.
    /// </summary>
    public class AuditDetailReferencesEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Audit Detail References";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var options = DetailReferenceOptions.Read(parameters);
            var includeOk = parameters.Value<bool?>("includeOk") ?? false;
            var limit = Math.Max(1, Math.Min(parameters.Value<int?>("limit") ?? 500, 5000));
            var offset = Math.Max(0, parameters.Value<int?>("offset") ?? 0);

            var index = ViewportIndex.Build(doc);
            var viewScope = ReadIdSet(parameters["viewIds"]);
            var sheetScope = parameters["sheetNumbers"] is JArray sheets && sheets.Count > 0
                ? new HashSet<string>(sheets.Select(t => DetailReferenceUtils.Normalize(t.ToString())))
                : null;

            bool InScope(ElementId hostViewId)
            {
                if (viewScope != null && !viewScope.Contains(hostViewId.GetValue()))
                    return false;
                if (sheetScope == null)
                    return true;
                var host = index.FindView(hostViewId);
                return host != null && sheetScope.Contains(DetailReferenceUtils.Normalize(host.Sheet.SheetNumber));
            }

            var bubbles = DetailReferenceUtils.CollectBubbles(doc, options, out var missingParameters)
                .Where(b => InScope(b.Instance.OwnerViewId))
                .ToList();

            var rows = new List<object>();
            int ok = 0, placeholder = 0, dead = 0;
            foreach (var bubble in bubbles)
            {
                var row = Classify(doc, bubble, index, options);
                switch (row.status)
                {
                    case "ok":
                        ok++;
                        if (!includeOk)
                            continue;
                        break;
                    case "placeholder":
                        placeholder++;
                        break;
                    default:
                        dead++;
                        break;
                }

                rows.Add(row);
            }

            var viewReferences = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ReferenceViewer)
                .WhereElementIsNotElementType()
                .Where(e => InScope(e.OwnerViewId))
                .OrderBy(e => e.Id.GetValue())
                .Select(e => DescribeViewReference(doc, e, index))
                .ToList();

            var page = rows.Skip(offset).Take(limit).ToList();
            return Ok(
                $"Audited {bubbles.Count} reference bubbles: {ok} ok, {placeholder} placeholder, {dead} dead; " +
                $"{viewReferences.Count} live view references.",
                new
                {
                    summary = new
                    {
                        bubbles = bubbles.Count,
                        ok,
                        placeholder,
                        dead,
                        skippedMissingParameters = missingParameters,
                        viewReferences = viewReferences.Count,
                        viewReferencesUnplaced = viewReferences.Count(v => v.status == "unplaced"),
                        viewReferencesMissingTarget = viewReferences.Count(v => v.status == "missing")
                    },
                    options = new
                    {
                        familyNameContains = options.FamilyNameContains,
                        detailNumberParam = options.DetailNumberParam,
                        sheetNumberParam = options.SheetNumberParam
                    },
                    total = rows.Count,
                    offset,
                    returned = page.Count,
                    truncated = offset + page.Count < rows.Count,
                    items = page,
                    viewReferences = viewReferences.Take(limit).ToList()
                });
        }

        private static AuditRow Classify(Document doc, ReferenceBubble bubble, ViewportIndex index,
            DetailReferenceOptions options)
        {
            var instance = bubble.Instance;
            var host = doc.GetElement(instance.OwnerViewId) as View;
            var hostPlacement = index.FindView(instance.OwnerViewId);
            var detail = bubble.DetailNumber;
            var sheet = bubble.SheetNumber;

            var row = new AuditRow
            {
                bubbleId = instance.Id.GetValue(),
                familyName = instance.Symbol?.FamilyName,
                typeName = instance.Symbol?.Name,
                hostViewId = instance.OwnerViewId.GetValue(),
                hostViewName = host?.Name,
                hostSheetNumber = hostPlacement?.Sheet.SheetNumber,
                detailNumber = detail,
                sheetNumber = sheet
            };

            if (options.Placeholders.Contains(DetailReferenceUtils.Normalize(detail))
                || options.Placeholders.Contains(DetailReferenceUtils.Normalize(sheet)))
            {
                row.status = "placeholder";
                return row;
            }

            var target = index.Find(sheet, detail);
            if (target != null)
            {
                row.status = "ok";
                row.targetViewId = target.View?.Id.GetValue();
                row.targetViewName = target.View?.Name;
                row.targetViewportId = target.Viewport.Id.GetValue();
                if (target.View != null && target.View.Id == instance.OwnerViewId)
                    row.reason = "selfReference";
                return row;
            }

            row.status = "dead";
            row.reason = index.SheetNumbers.Contains(DetailReferenceUtils.Normalize(sheet))
                ? "detailNotOnSheet"
                : "sheetNotFound";
            return row;
        }

        private static ViewReferenceRow DescribeViewReference(Document doc, Element element, ViewportIndex index)
        {
            var host = doc.GetElement(element.OwnerViewId) as View;
            var targetId = element.get_Parameter(BuiltInParameter.REFERENCE_VIEWER_TARGET_VIEW)?.AsElementId();
            var target = targetId != null && targetId != ElementId.InvalidElementId
                ? doc.GetElement(targetId) as View
                : null;
            var placement = target == null ? null : index.FindView(target.Id);
            return new ViewReferenceRow
            {
                id = element.Id.GetValue(),
                typeName = element.Name,
                hostViewId = element.OwnerViewId.GetValue(),
                hostViewName = host?.Name,
                targetViewId = target?.Id.GetValue(),
                targetViewName = target?.Name,
                targetSheetNumber = placement?.Sheet.SheetNumber,
                targetDetailNumber = placement?.DetailNumber,
                status = target == null ? "missing" : placement == null ? "unplaced" : "ok"
            };
        }

        internal static HashSet<long> ReadIdSet(JToken token)
        {
            if (!(token is JArray array) || array.Count == 0)
                return null;
            return new HashSet<long>(array.Select(t => t.Value<long>()));
        }

        // Lower-case members: serialised as-is on the wire.
        private sealed class AuditRow
        {
            public long bubbleId { get; set; }
            public string familyName { get; set; }
            public string typeName { get; set; }
            public long hostViewId { get; set; }
            public string hostViewName { get; set; }
            public string hostSheetNumber { get; set; }
            public string detailNumber { get; set; }
            public string sheetNumber { get; set; }
            public string status { get; set; }
            public string reason { get; set; }
            public long? targetViewId { get; set; }
            public string targetViewName { get; set; }
            public long? targetViewportId { get; set; }
        }

        private sealed class ViewReferenceRow
        {
            public long id { get; set; }
            public string typeName { get; set; }
            public long hostViewId { get; set; }
            public string hostViewName { get; set; }
            public long? targetViewId { get; set; }
            public string targetViewName { get; set; }
            public string targetSheetNumber { get; set; }
            public string targetDetailNumber { get; set; }
            public string status { get; set; }
        }
    }
}
