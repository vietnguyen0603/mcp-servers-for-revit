using System.Text.RegularExpressions;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services.AnnotationComponents;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Library
{
    /// <summary>
    ///     Library-wide QA of typical details (drafting views by default):
    ///     stale manual reference bubbles (same classification as
    ///     audit_detail_references), unplaced views, naming breaks,
    ///     non-standard line styles / text / dimension / fill types, CAD-import
    ///     leftovers, overlapping text, numeric dimension overrides and sheet
    ///     (drawing list) consistency. Read-only; no transaction.
    /// </summary>
    public class AuditDetailLibraryEventHandler : JsonParameterEventHandler
    {
        internal static readonly string[] AllChecks =
        {
            "references", "placement", "naming", "lineStyles", "textTypes", "dimensionTypes", "filledRegionTypes",
            "cadImports", "textOverlaps", "dimensionOverrides", "drawingList"
        };

        private static readonly Regex NumericOverride =
            new Regex("^\\s*\\d+'?-?\\s*\\d*(\\s\\d+/\\d+)?\"?\\s*$", RegexOptions.CultureInvariant);

        private static readonly Regex DuplicateSuffix =
            new Regex(@"(\s*-?\s*copy(\s*\d+)?|\s*\(\d+\)|\s+\d+|(?<=[A-Za-z])\d)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public override string GetName() => "Audit Detail Library";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var requested = parameters["checks"] is JArray c && c.Count > 0
                ? c.Select(t => t.ToString()).ToList()
                : AllChecks.ToList();
            var unknown = requested.Where(r => !AllChecks.Contains(r)).ToList();
            if (unknown.Count > 0)
                return Fail($"Unknown checks: {string.Join(", ", unknown)}. Expected: {string.Join(", ", AllChecks)}.");

            var context = new AuditContext(doc, parameters);
            var details = new JObject();
            var summary = new JObject();
            foreach (var check in AllChecks.Where(requested.Contains))
            {
                JObject result;
                try
                {
                    switch (check)
                    {
                        case "references":
                            result = References(context);
                            break;
                        case "placement":
                            result = Placement(context);
                            break;
                        case "naming":
                            result = Naming(context);
                            break;
                        case "lineStyles":
                            result = TypeUsage(context, "lineStyles",
                                DraftingLibraryUtils.DetailCurves(doc, context.ViewIds)
                                    .Select(e => ((Element)e, e.LineStyle.Id, e.LineStyle.Name)));
                            break;
                        case "textTypes":
                            result = TypeUsage(context, "textTypes",
                                DraftingLibraryUtils.Owned<TextNote>(doc, context.ViewIds)
                                    .Select(e => ((Element)e, e.GetTypeId(), doc.GetElement(e.GetTypeId())?.Name)));
                            break;
                        case "dimensionTypes":
                            result = TypeUsage(context, "dimensionTypes",
                                DraftingLibraryUtils.Dimensions(doc, context.ViewIds)
                                    .Select(e => ((Element)e, e.GetTypeId(), doc.GetElement(e.GetTypeId())?.Name)));
                            break;
                        case "filledRegionTypes":
                            result = TypeUsage(context, "filledRegionTypes",
                                DraftingLibraryUtils.Owned<FilledRegion>(doc, context.ViewIds)
                                    .Select(e => ((Element)e, e.GetTypeId(), doc.GetElement(e.GetTypeId())?.Name)));
                            break;
                        case "cadImports":
                            result = CadImports(context);
                            break;
                        case "textOverlaps":
                            result = TextOverlaps(context);
                            break;
                        case "dimensionOverrides":
                            result = DimensionOverrides(context);
                            break;
                        default:
                            result = DrawingList(context);
                            break;
                    }
                }
                catch (Exception ex) when (!(ex is ArgumentException))
                {
                    result = new JObject { ["count"] = 0, ["error"] = ex.Message };
                }

                details[check] = result;
                summary[check] = result.Value<int?>("count") ?? 0;
            }

            var message = $"Audited {context.Views.Count} views of '{doc.Title}': " +
                          string.Join(", ", summary.Properties().Select(p => $"{p.Name} {p.Value}")) + ".";
            return Ok(message, new JObject
            {
                ["documentTitle"] = doc.Title,
                ["generatedAt"] = DateTime.Now.ToString("yyyy-MM-dd"),
                ["views"] = context.Views.Count,
                ["limitPerCheck"] = context.Limit,
                ["summary"] = summary,
                ["details"] = details
            });
        }

        private static JObject Section(int count, IEnumerable<JToken> items, int total, int limit, JObject extra = null)
        {
            var list = items.Take(limit).ToList();
            var section = new JObject { ["count"] = count };
            if (extra != null)
                foreach (var p in extra.Properties())
                    section[p.Name] = p.Value;
            section["items"] = new JArray(list);
            section["truncated"] = total > list.Count;
            return section;
        }

        private static JObject References(AuditContext ctx)
        {
            var options = DetailReferenceOptions.Read(ctx.Parameters);
            var bubbles = DetailReferenceUtils.CollectBubbles(ctx.Doc, options, out var missingParameters)
                .Where(b => ctx.ViewIds.Contains(b.Instance.OwnerViewId.GetValue()))
                .ToList();
            var rows = bubbles
                .Select(b => AuditDetailReferencesEventHandler.Classify(ctx.Doc, b, ctx.Index, options))
                .ToList();
            var dead = rows.Where(r => r.status == "dead").ToList();
            var bySheet = dead.GroupBy(r => r.sheetNumber ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .Select(g => new JObject { ["sheetNumber"] = g.Key, ["bubbles"] = g.Count() });
            return Section(dead.Count, dead.Select(JObject.FromObject), dead.Count, ctx.Limit, new JObject
            {
                ["bubbles"] = rows.Count,
                ["ok"] = rows.Count(r => r.status == "ok"),
                ["placeholder"] = rows.Count(r => r.status == "placeholder"),
                ["dead"] = dead.Count,
                ["skippedMissingParameters"] = missingParameters,
                ["deadBySheet"] = new JArray(bySheet)
            });
        }

        private static JObject Placement(AuditContext ctx)
        {
            var unplaced = ctx.Views.Where(v => ctx.Index.FindView(v.Id) == null).ToList();
            return Section(unplaced.Count,
                unplaced.Select(v => new JObject { ["viewId"] = v.Id.GetValue(), ["viewName"] = v.Name }),
                unplaced.Count, ctx.Limit, new JObject { ["placed"] = ctx.Views.Count - unplaced.Count });
        }

        private static JObject Naming(AuditContext ctx)
        {
            var standards = ctx.Parameters["standards"] as JObject;
            var pattern = DraftingLibraryUtils.CompileRegex(
                standards?.Value<string>("namePattern") ?? DraftingLibraryUtils.DefaultNamePattern, "namePattern");
            var off = ctx.Views.Where(v => !pattern.IsMatch(v.Name)).ToList();

            // A name with a copy/number suffix whose base name is another view's name.
            var names = new HashSet<string>(DraftingLibraryUtils.DraftingViews(ctx.Doc).Select(v => v.Name.Trim()),
                StringComparer.OrdinalIgnoreCase);
            var duplicates = ctx.Views
                .Select(v => new { view = v, baseName = DuplicateSuffix.Replace(v.Name.Trim(), string.Empty).Trim() })
                .Where(x => x.baseName.Length > 0 && !string.Equals(x.baseName, x.view.Name.Trim(),
                    StringComparison.OrdinalIgnoreCase) && names.Contains(x.baseName))
                .ToList();

            var items = off.Select(v => new JObject
                {
                    ["viewId"] = v.Id.GetValue(), ["viewName"] = v.Name, ["issue"] = "pattern"
                })
                .Concat(duplicates.Select(x => new JObject
                {
                    ["viewId"] = x.view.Id.GetValue(), ["viewName"] = x.view.Name, ["issue"] = "likelyDuplicate",
                    ["duplicateOf"] = x.baseName
                }))
                .ToList();
            return Section(off.Count + duplicates.Count, items, items.Count, ctx.Limit, new JObject
            {
                ["pattern"] = pattern.ToString(),
                ["offPattern"] = off.Count,
                ["likelyDuplicates"] = duplicates.Count
            });
        }

        /// <summary>
        ///     Usage of a style/type in scope. With an allowed list in
        ///     standards.&lt;kind&gt; every other name is non-standard; without one,
        ///     names used fewer than standards.minUses times (default 5) are.
        /// </summary>
        private static JObject TypeUsage(AuditContext ctx, string kind,
            IEnumerable<(Element element, ElementId typeId, string name)> usage)
        {
            var standards = ctx.Parameters["standards"] as JObject;
            var allowed = NameMatcher.From(standards?[kind], kind);
            var minUses = Math.Max(1, standards?.Value<int?>("minUses") ?? 5);

            var groups = usage
                .GroupBy(u => u.typeId.GetValue())
                .Select(g => new
                {
                    id = g.Key,
                    name = g.First().name ?? "?",
                    uses = g.Count(),
                    elements = g.Select(x => x.element).ToList()
                })
                .ToList();
            var flagged = groups
                .Where(g => allowed != null ? !allowed.IsMatch(g.name) : g.uses < minUses)
                .OrderByDescending(g => g.uses).ThenBy(g => g.name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var items = flagged.Select(g =>
            {
                var views = g.elements.GroupBy(e => e.OwnerViewId.GetValue())
                    .OrderByDescending(v => v.Count()).ToList();
                return new JObject
                {
                    ["name"] = g.name,
                    ["id"] = g.id,
                    ["uses"] = g.uses,
                    ["views"] = views.Count,
                    ["sampleViews"] = new JArray(views.Take(3).Select(v => ctx.ViewName(v.First().OwnerViewId))),
                    ["sampleIds"] = new JArray(g.elements.Take(5).Select(e => e.Id.GetValue()))
                };
            });
            return Section(flagged.Count, items, flagged.Count, ctx.Limit, new JObject
            {
                ["mode"] = allowed != null ? "allowedList" : "rareUsage",
                ["minUses"] = allowed != null ? (JToken)null : minUses,
                ["typesUsed"] = groups.Count,
                ["elements"] = flagged.Sum(g => g.uses)
            });
        }

        private static JObject CadImports(AuditContext ctx)
        {
            var doc = ctx.Doc;
            var imports = DraftingLibraryUtils.Owned<ImportInstance>(doc, ctx.ViewIds);
            var byView = imports.GroupBy(i => i.OwnerViewId.GetValue())
                .OrderByDescending(g => g.Count()).ThenBy(g => ctx.ViewName(g.First().OwnerViewId))
                .Select(g => (JToken)new JObject
                {
                    ["kind"] = "importInstance",
                    ["viewId"] = g.Key,
                    ["viewName"] = ctx.ViewName(g.First().OwnerViewId),
                    ["count"] = g.Count(),
                    ["ids"] = new JArray(g.Take(10).Select(i => i.Id.GetValue())),
                    ["names"] = new JArray(g.Select(i => i.Category?.Name ?? i.Name).Distinct().Take(10)),
                    ["linked"] = g.Any(i => i.IsLinked)
                })
                .ToList();

            var regionUses = DraftingLibraryUtils.Owned<FilledRegion>(doc, ctx.ViewIds)
                .GroupBy(r => r.GetTypeId().GetValue()).ToDictionary(g => g.Key, g => g.Count());
            var allRegionTypes = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(FilledRegion))
                .WhereElementIsNotElementType().Select(r => r.GetTypeId().GetValue()));
            var cadFills = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType))
                .Where(t => GetDraftingStandardsEventHandler.IsCadName(t.Name))
                .Select(t => new
                {
                    t,
                    uses = regionUses.TryGetValue(t.Id.GetValue(), out var n) ? n : 0,
                    usedAnywhere = allRegionTypes.Contains(t.Id.GetValue())
                })
                .OrderByDescending(x => x.uses).ThenBy(x => x.t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var fillItems = cadFills.Where(x => x.uses > 0).Select(x => (JToken)new JObject
            {
                ["kind"] = "cadFillType",
                ["id"] = x.t.Id.GetValue(),
                ["name"] = x.t.Name,
                ["uses"] = x.uses
            });

            var importPatterns = new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement))
                .Where(p => p.Name.StartsWith("IMPORT-", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var items = byView.Concat(fillItems).ToList();
            return Section(imports.Count + cadFills.Sum(x => x.uses), items, items.Count, ctx.Limit, new JObject
            {
                ["importInstances"] = imports.Count,
                ["viewsWithImports"] = byView.Count,
                ["cadFillTypes"] = cadFills.Count,
                ["cadFillRegions"] = cadFills.Sum(x => x.uses),
                ["unusedCadFillTypes"] = cadFills.Count(x => !x.usedAnywhere),
                ["importLinePatterns"] = importPatterns.Count,
                ["importLinePatternNames"] = new JArray(importPatterns.Take(20))
            });
        }

        private static JObject TextOverlaps(AuditContext ctx)
        {
            var minArea = Math.Max(0, ctx.Parameters.Value<double?>("overlapMinAreaMm2") ?? 1.0);
            var notesByView = DraftingLibraryUtils.Owned<TextNote>(ctx.Doc, ctx.ViewIds)
                .GroupBy(t => t.OwnerViewId.GetValue())
                .ToDictionary(g => g.Key, g => g.ToList());

            var hits = new List<JObject>();
            var viewsWithOverlaps = 0;
            foreach (var view in ctx.Views)
            {
                if (!notesByView.TryGetValue(view.Id.GetValue(), out var notes) || notes.Count < 2)
                    continue;
                var paperMmPerFoot = DocumentationUtils.MmPerFoot / Math.Max(1, view.Scale);
                var byId = notes.ToDictionary(n => n.Id.GetValue());
                var extents = notes.Select(n => FindTagOverlapsEventHandler.Extent.Of(n, view, 0))
                    .Where(x => x != null).ToList();
                var pairs = new List<(FindTagOverlapsEventHandler.Extent a, FindTagOverlapsEventHandler.Extent b, double area)>();
                FindTagOverlapsEventHandler.Sweep(extents, extents, true, pairs);
                var found = pairs
                    .Select(p => new { p.a, p.b, mm2 = p.area * paperMmPerFoot * paperMmPerFoot })
                    .Where(p => p.mm2 >= minArea)
                    .OrderByDescending(p => p.mm2)
                    .ToList();
                if (found.Count > 0)
                    viewsWithOverlaps++;
                hits.AddRange(found.Select(p => new JObject
                {
                    ["viewId"] = view.Id.GetValue(),
                    ["viewName"] = view.Name,
                    ["aId"] = p.a.Id,
                    ["bId"] = p.b.Id,
                    ["aText"] = DraftingLibraryUtils.Truncate(byId[p.a.Id].Text, 40),
                    ["bText"] = DraftingLibraryUtils.Truncate(byId[p.b.Id].Text, 40),
                    ["overlapAreaPaperMm2"] = Math.Round(p.mm2, 2)
                }));
            }

            return Section(hits.Count, hits, hits.Count, ctx.Limit, new JObject
            {
                ["minAreaPaperMm2"] = minArea,
                ["viewsWithOverlaps"] = viewsWithOverlaps
            });
        }

        private static JObject DimensionOverrides(AuditContext ctx)
        {
            var dims = DraftingLibraryUtils.Dimensions(ctx.Doc, ctx.ViewIds);
            var hits = new List<JObject>();
            var overrides = 0;
            foreach (var d in dims)
            {
                try
                {
                    foreach (var o in DraftingLibraryUtils.Overrides(d))
                    {
                        if (string.IsNullOrEmpty(o.valueOverride))
                            continue;
                        overrides++;
                        if (!NumericOverride.IsMatch(o.valueOverride))
                            continue;
                        hits.Add(new JObject
                        {
                            ["viewId"] = d.OwnerViewId.GetValue(),
                            ["viewName"] = ctx.ViewName(d.OwnerViewId),
                            ["dimensionId"] = d.Id.GetValue(),
                            ["segment"] = o.segment,
                            ["override"] = o.valueOverride,
                            ["actual"] = o.value
                        });
                    }
                }
                catch (Exception)
                {
                    // Dimensions with invalid references cannot report segments.
                }
            }

            var blank = dims.Where(d => (ctx.Doc.GetElement(d.GetTypeId())?.Name ?? string.Empty)
                    .TrimEnd().EndsWith("Blank", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return Section(hits.Count, hits, hits.Count, ctx.Limit, new JObject
            {
                ["dimensions"] = dims.Count,
                ["overrides"] = overrides,
                ["numericOverrides"] = hits.Count,
                ["blankTypeDimensions"] = blank.Count,
                ["blankTypeViews"] = blank.Select(d => d.OwnerViewId.GetValue()).Distinct().Count()
            });
        }

        /// <summary>
        ///     Sheet-level consistency: empty sheets, sheets left out of the
        ///     sheet list, duplicate sheet names and gaps in numeric detail
        ///     numbers. Scoped by sheetNumbers when given.
        /// </summary>
        private static JObject DrawingList(AuditContext ctx)
        {
            var doc = ctx.Doc;
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsTemplate && !s.IsPlaceholder)
                .ToList();
            if (ctx.Parameters["sheetNumbers"] is JArray wanted && wanted.Count > 0)
            {
                var set = new HashSet<string>(wanted.Select(t => DetailReferenceUtils.Normalize(t.ToString())));
                sheets = sheets.Where(s => set.Contains(DetailReferenceUtils.Normalize(s.SheetNumber))).ToList();
            }

            sheets = sheets.OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase).ToList();
            var schedulesOn = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance))
                .Select(e => e.OwnerViewId.GetValue()));
            var items = new List<JObject>();

            JObject Row(ViewSheet s, string issue, string detail) => new JObject
            {
                ["sheetId"] = s.Id.GetValue(),
                ["sheetNumber"] = s.SheetNumber,
                ["sheetName"] = s.Name,
                ["issue"] = issue,
                ["detail"] = detail
            };

            foreach (var s in sheets)
            {
                var viewports = s.GetAllViewports();
                if (viewports.Count == 0 && !schedulesOn.Contains(s.Id.GetValue()))
                    items.Add(Row(s, "emptySheet", "no viewports or schedules"));

                var inList = s.get_Parameter(BuiltInParameter.SHEET_SCHEDULED);
                if (inList != null && inList.HasValue && inList.AsInteger() == 0)
                    items.Add(Row(s, "notInSheetList", "'Appears In Sheet List' is off"));

                var numbers = viewports
                    .Select(id => doc.GetElement(id) as Viewport)
                    .Where(v => v != null)
                    .Select(v => int.TryParse(DetailReferenceUtils.ViewportDetailNumber(v), out var n) ? n : 0)
                    .Where(n => n > 0)
                    .ToList();
                if (numbers.Count > 1)
                {
                    var missing = Enumerable.Range(1, numbers.Max()).Except(numbers).ToList();
                    if (missing.Count > 0 && missing.Count <= numbers.Max() / 2)
                        items.Add(Row(s, "detailNumberGaps", "missing " + string.Join(", ", missing.Take(20))));
                }
            }

            foreach (var g in sheets.GroupBy(s => (s.Name ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Key.Length > 0 && g.Count() > 1))
                items.Add(Row(g.First(), "duplicateSheetName",
                    "also on " + string.Join(", ", g.Skip(1).Select(s => s.SheetNumber))));

            return Section(items.Count, items, items.Count, ctx.Limit, new JObject
            {
                ["sheets"] = sheets.Count,
                ["emptySheets"] = items.Count(i => i.Value<string>("issue") == "emptySheet"),
                ["notInSheetList"] = items.Count(i => i.Value<string>("issue") == "notInSheetList"),
                ["detailNumberGaps"] = items.Count(i => i.Value<string>("issue") == "detailNumberGaps"),
                ["duplicateSheetNames"] = items.Count(i => i.Value<string>("issue") == "duplicateSheetName")
            });
        }

        private sealed class AuditContext
        {
            public AuditContext(Document doc, JObject parameters)
            {
                Doc = doc;
                Parameters = parameters;
                Limit = Math.Max(1, Math.Min(parameters.Value<int?>("limitPerCheck") ?? 25, 1000));
                Index = ViewportIndex.Build(doc);
                Views = DraftingLibraryUtils.ResolveViews(doc, parameters, Index);
                ViewIds = new HashSet<long>(Views.Select(v => v.Id.GetValue()));
                ViewName = DraftingLibraryUtils.ViewNamer(doc);
            }

            public Document Doc { get; }
            public JObject Parameters { get; }
            public int Limit { get; }
            public ViewportIndex Index { get; }
            public List<View> Views { get; }
            public HashSet<long> ViewIds { get; }
            public Func<ElementId, string> ViewName { get; }
        }
    }
}
