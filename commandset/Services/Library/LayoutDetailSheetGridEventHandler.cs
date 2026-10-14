using System.Text.RegularExpressions;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services.Views;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Library
{
    /// <summary>
    ///     Office 12-cell detail sheet grid. The packing runs on the MCP server;
    ///     this handler measures (inner border + viewport boxes and titles),
    ///     applies a plan (move/recreate viewports, titles, detail numbers via
    ///     temporary numbers, new sheets, reference remap, empty sheet clean-up)
    ///     and verifies the result (outside border, overlaps, broken references).
    /// </summary>
    public class LayoutDetailSheetGridEventHandler : JsonParameterEventHandler
    {
        private const int MaxListed = 100;

        public override string GetName() => "Layout Detail Sheet Grid";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var mode = (parameters.Value<string>("mode") ?? "measure").Trim().ToLowerInvariant();
            switch (mode)
            {
                case "measure":
                    return Measure(uiDoc.Document, parameters);
                case "apply":
                    return Apply(uiDoc, parameters);
                case "verify":
                    return Verify(uiDoc.Document, parameters);
                default:
                    throw new ArgumentException("'mode' must be measure, apply or verify (plan runs on the MCP server).");
            }
        }

        // ---------------------------------------------------------------- measure

        private sealed class Measured
        {
            public ViewSheet Sheet;
            public Viewport Viewport;
            public bool Titled;
            public JObject Json;
            public double LineLength;
        }

        private static AIResult<object> Measure(Document doc, JObject parameters)
        {
            var warnings = new List<string>();
            var sheets = DetailSheetGridUtils.SelectSheets(doc, parameters);
            if (sheets.Count == 0)
                return Fail("No sheets matched the selection.");
            var border = DetailSheetGridUtils.ReadBorder(doc, parameters, sheets, warnings, out var titleBlock);

            var rows = new List<Measured>();
            foreach (var sheet in sheets)
            foreach (var id in sheet.GetAllViewports())
            {
                if (!(doc.GetElement(id) is Viewport viewport))
                    continue;
                var box = viewport.GetBoxOutline();
                var titled = DetailSheetGridUtils.IsTitled(doc, viewport);
                var json = new JObject
                {
                    ["viewportId"] = viewport.Id.GetValue(),
                    ["viewId"] = viewport.ViewId.GetValue(),
                    ["viewName"] = DocumentationUtils.ViewName(doc, viewport.ViewId),
                    ["typeId"] = viewport.GetTypeId().GetValue(),
                    ["titled"] = titled,
                    ["detailNumber"] = ViewportUtils.DetailNumber(viewport),
                    ["box"] = DetailSheetGridUtils.BoxMm(box)
                };
                var row = new Measured { Sheet = sheet, Viewport = viewport, Titled = titled, Json = json };
                var label = titled ? DetailSheetGridUtils.LabelOutline(viewport) : null;
                if (label != null)
                {
                    json["titleWidth"] = DocumentationUtils.FeetToMm(label.MaximumPoint.X - label.MinimumPoint.X);
                    json["labelHeight"] = DocumentationUtils.FeetToMm(label.MaximumPoint.Y - label.MinimumPoint.Y);
#if REVIT2022_OR_GREATER
                    // K = label min - (box min + LabelOffset); extra = label width - line length
                    var offset = viewport.LabelOffset;
                    row.LineLength = viewport.LabelLineLength;
                    json["labelK"] = new JArray(
                        DocumentationUtils.FeetToMm(label.MinimumPoint.X - box.MinimumPoint.X - offset.X),
                        DocumentationUtils.FeetToMm(label.MinimumPoint.Y - box.MinimumPoint.Y - offset.Y));
                    json["labelExtra"] = DocumentationUtils.FeetToMm(label.MaximumPoint.X - label.MinimumPoint.X - row.LineLength);
#endif
                }

                rows.Add(row);
            }

            MeasureTitleText(doc, rows, warnings);

            var selected = new HashSet<long>(sheets.Select(s => s.Id.GetValue()));
            var response = new JObject
            {
                ["border"] = border.ToMm(),
                ["borderSource"] = border.Source,
                ["titleBlock"] = titleBlock == null
                    ? null
                    : new JObject
                    {
                        ["typeId"] = titleBlock.GetTypeId().GetValue(),
                        ["name"] = titleBlock.Symbol.FamilyName + ": " + titleBlock.Name
                    },
                ["sheets"] = new JArray(sheets.Select(s => new JObject
                {
                    ["id"] = s.Id.GetValue(),
                    ["number"] = s.SheetNumber,
                    ["name"] = s.Name,
                    ["viewports"] = new JArray(rows.Where(r => r.Sheet.Id == s.Id).Select(r => r.Json))
                })),
                ["otherSheetNumbers"] = new JArray(DetailSheetGridUtils.AllSheets(doc)
                    .Where(s => !selected.Contains(s.Id.GetValue()))
                    .Select(s => s.SheetNumber)
                    .OrderBy(n => n, StringComparer.Ordinal)),
                ["warnings"] = new JArray(warnings)
            };
            if (border.Lines != null)
                response["borderLines"] = border.Lines;
            return Ok($"Measured {rows.Count} viewports on {sheets.Count} sheets.", response);
        }

        /// <summary>
        ///     The title text width: every title line shrunk to 1 mm inside a
        ///     rolled-back transaction (Revit 2022+). 'labelExtra' is only kept
        ///     when the original line was longer than the text.
        /// </summary>
        private static void MeasureTitleText(Document doc, List<Measured> rows, List<string> warnings)
        {
#if REVIT2022_OR_GREATER
            var titled = rows.Where(r => r.Titled && r.Json["labelK"] != null).ToList();
            if (titled.Count == 0 || doc.IsReadOnly || doc.IsModifiable)
                return;
            try
            {
                using (var transaction = DocumentationUtils.StartTransaction(doc, "MCP: Measure Titles"))
                {
                    try
                    {
                        foreach (var row in titled)
                            row.Viewport.LabelLineLength = DocumentationUtils.MmToFeet(1);
                        doc.Regenerate();
                        foreach (var row in titled)
                        {
                            var label = DetailSheetGridUtils.LabelOutline(row.Viewport);
                            if (label == null)
                                continue;
                            var width = label.MaximumPoint.X - label.MinimumPoint.X;
                            row.Json["titleWidth"] = DocumentationUtils.FeetToMm(width);
                            var extra = row.Json.Value<double>("labelExtra");
                            if (extra + DocumentationUtils.FeetToMm(row.LineLength) < DocumentationUtils.FeetToMm(width) + 1)
                                row.Json["labelExtra"] = -1;
                        }
                    }
                    finally
                    {
                        transaction.RollBack();
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Title text widths could not be measured ({ex.Message}); using the current title outlines.");
            }
#else
            warnings.Add("Title offsets and line lengths need Revit 2022 or later; titles keep their current settings.");
#endif
        }

        // ---------------------------------------------------------------- apply

        private sealed class Entry
        {
            public Viewport Viewport;
            public ElementId ViewId;
            public ElementId TypeId;
            public ViewSheet Target;
            public XYZ Center;
            public string Number;
            public XYZ LabelOffset;
            public double? LabelLineLength;
            public string OldSheet;
            public string OldDetail;
            public bool Moves;
        }

        private static AIResult<object> Apply(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var dryRun = parameters.Value<bool?>("dryRun") ?? true;
            var deleteEmpty = parameters.Value<bool?>("deleteEmptySheets") ?? true;
            var remap = parameters.Value<bool?>("remapReferences") ?? true;
            var planSheets = DocumentationUtils.RequireArray(parameters, "sheets");
            var oldSheets = ((parameters["oldSheetIds"] as JArray) ?? new JArray())
                .Select(t => DocumentationUtils.GetElement<ViewSheet>(doc, t.Value<long>()))
                .Where(s => s != null)
                .ToList();
            var oldIds = new HashSet<long>(oldSheets.Select(s => s.Id.GetValue()));
            var byNumber = new Dictionary<string, ViewSheet>(StringComparer.OrdinalIgnoreCase);
            foreach (var sheet in DetailSheetGridUtils.AllSheets(doc))
                byNumber[sheet.SheetNumber] = sheet;

            var log = new JObject();
            var created = new List<string>();
            var renamed = new List<string>();
            var warnings = new List<string>();
            var entries = new List<Entry>();
            var step = "validate";

            using (var transaction = DocumentationUtils.StartTransaction(doc, "MCP: Layout Detail Sheet Grid"))
            {
                try
                {
                    var seen = new HashSet<long>();
                    var targets = new List<ViewSheet>();
                    foreach (var planSheet in planSheets)
                    {
                        step = "sheets";
                        var number = planSheet.Value<string>("number")?.Trim();
                        var name = planSheet.Value<string>("name") ?? string.Empty;
                        if (string.IsNullOrEmpty(number))
                            throw new ArgumentException("Every planned sheet needs a 'number'.");
                        var target = DocumentationUtils.GetElement<ViewSheet>(doc, DocumentationUtils.ReadId(planSheet, "sheetId"));
                        if (target == null && byNumber.TryGetValue(number, out var existing))
                        {
                            if (!oldIds.Contains(existing.Id.GetValue()))
                                throw new InvalidOperationException($"Sheet {number} exists outside the selection; it is not reused.");
                            target = existing;
                        }

                        if (target == null)
                        {
                            var template = DocumentationUtils.GetElement<ViewSheet>(doc, DocumentationUtils.ReadId(planSheet, "templateSheetId"))
                                           ?? oldSheets.FirstOrDefault()
                                           ?? throw new ArgumentException($"No template sheet for new sheet {number}.");
                            target = CreateSheet(doc, template, number, name, warnings);
                            byNumber[number] = target;
                            created.Add(number);
                        }
                        else if (!string.IsNullOrEmpty(name) && target.Name != name)
                        {
                            target.Name = name;
                            renamed.Add(number);
                        }

                        targets.Add(target);
                        step = "read plan";
                        foreach (var item in (planSheet["viewports"] as JArray) ?? new JArray())
                        {
                            var viewport = DocumentationUtils.GetElement<Viewport>(doc, DocumentationUtils.ReadId(item, "viewportId"))
                                           ?? throw new ArgumentException($"Viewport {item["viewportId"]} no longer exists; plan again.");
                            if (!seen.Add(viewport.Id.GetValue()))
                                throw new ArgumentException($"Viewport {viewport.Id.GetValue()} is planned twice.");
                            if (viewport.ViewId.GetValue() != item.Value<long>("viewId"))
                                throw new ArgumentException($"Viewport {viewport.Id.GetValue()} shows another view now; plan again.");
                            var detail = item.Value<string>("detailNumber")?.Trim();
                            if (string.IsNullOrEmpty(detail))
                                throw new ArgumentException($"Viewport {viewport.Id.GetValue()} has no planned detail number.");
                            var typeId = DocumentationUtils.ReadId(item, "typeId");
                            entries.Add(new Entry
                            {
                                Viewport = viewport,
                                ViewId = viewport.ViewId,
                                TypeId = typeId?.ToRevitElementId(),
                                Target = target,
                                Center = ViewportUtils.ReadPoint2(item["center"], "center")
                                         ?? throw new ArgumentException($"Viewport {viewport.Id.GetValue()} has no planned centre."),
                                Number = detail,
                                LabelOffset = ViewportUtils.ReadPoint2(item["labelOffset"], "labelOffset"),
                                LabelLineLength = item.Value<double?>("labelLineLength"),
                                OldSheet = item["old"]?.Value<string>("sheetNumber") ?? (doc.GetElement(viewport.SheetId) as ViewSheet)?.SheetNumber,
                                OldDetail = item["old"]?.Value<string>("detailNumber") ?? ViewportUtils.DetailNumber(viewport),
                                Moves = viewport.SheetId != target.Id
                            });
                        }
                    }

                    var duplicate = entries
                        .GroupBy(e => e.Target.Id.GetValue() + "\u0001" + DetailReferenceUtils.Normalize(e.Number))
                        .FirstOrDefault(g => g.Count() > 1);
                    if (duplicate != null)
                        throw new ArgumentException($"Detail number '{duplicate.First().Number}' is planned twice on {duplicate.First().Target.SheetNumber}.");

                    step = "temporary numbers";
                    foreach (var e in entries.Where(e => !e.Moves))
                        ViewportUtils.WriteDetailNumber(e.Viewport, DetailSheetGridUtils.TemporaryPrefix + e.Viewport.Id.GetValue());

                    step = "remove viewports that change sheet";
                    foreach (var e in entries.Where(e => e.Moves))
                        doc.Delete(e.Viewport.Id);

                    step = "place viewports on their new sheets";
                    foreach (var e in entries.Where(e => e.Moves))
                    {
                        if (!Viewport.CanAddViewToSheet(doc, e.Target.Id, e.ViewId))
                            throw new InvalidOperationException($"View {e.ViewId.GetValue()} cannot be placed on {e.Target.SheetNumber}.");
                        e.Viewport = Viewport.Create(doc, e.Target.Id, e.ViewId, e.Center);
                        if (e.TypeId != null && e.Viewport.GetTypeId() != e.TypeId)
                            e.Viewport.ChangeTypeId(e.TypeId);
                        ViewportUtils.WriteDetailNumber(e.Viewport, DetailSheetGridUtils.TemporaryPrefix + e.Viewport.Id.GetValue());
                    }

                    doc.Regenerate();
                    step = "positions";
                    foreach (var e in entries)
                        e.Viewport.SetBoxCenter(e.Center);

                    step = "detail numbers";
                    foreach (var e in entries)
                        ViewportUtils.WriteDetailNumber(e.Viewport, e.Number);

                    step = "titles";
                    var titles = 0;
#if REVIT2022_OR_GREATER
                    foreach (var e in entries.Where(e => e.LabelOffset != null || e.LabelLineLength != null))
                    {
                        if (e.LabelOffset != null)
                            e.Viewport.LabelOffset = e.LabelOffset;
                        if (e.LabelLineLength != null)
                            e.Viewport.LabelLineLength = DocumentationUtils.MmToFeet(Math.Max(0, e.LabelLineLength.Value));
                        titles++;
                    }
#else
                    if (entries.Any(e => e.LabelOffset != null || e.LabelLineLength != null))
                        warnings.Add("Title offsets and line lengths need Revit 2022 or later; not set.");
#endif

                    var references = new JObject();
                    if (remap)
                    {
                        step = "reference bubbles";
                        var map = new Dictionary<string, Entry>();
                        foreach (var e in entries)
                        {
                            if (!DetailReferenceUtils.SameValue(e.OldSheet, e.Target.SheetNumber) || !DetailReferenceUtils.SameValue(e.OldDetail, e.Number))
                                map[DetailReferenceUtils.Key(e.OldSheet, e.OldDetail)] = e;
                        }

                        references["bubbles"] = RemapBubbles(doc, parameters, map, warnings);
                        step = "text references";
                        var pattern = DetailSheetGridUtils.SheetAlternation(parameters.Value<string>("textSheetPattern"),
                            oldSheets.Select(s => s.SheetNumber));
                        references["textNotes"] = RemapText(doc, pattern, map, entries, references, warnings);
                    }

                    step = "empty sheets";
                    doc.Regenerate();
                    var deleted = new List<string>();
                    var kept = new List<string>();
                    var targetIds = new HashSet<long>(targets.Select(t => t.Id.GetValue()));
                    var activeId = uiDoc.ActiveView?.Id;
                    foreach (var sheet in oldSheets.Where(s => s.IsValidObject && !targetIds.Contains(s.Id.GetValue())))
                    {
                        var others = OtherContent(doc, sheet);
                        if (!deleteEmpty)
                            kept.Add(sheet.SheetNumber + " (deleteEmptySheets=false)");
                        else if (sheet.Id == activeId)
                            kept.Add(sheet.SheetNumber + " (active view)");
                        else if (sheet.GetAllViewports().Count > 0)
                            kept.Add($"{sheet.SheetNumber} ({sheet.GetAllViewports().Count} viewports)");
                        else if (others.Count > 0)
                            kept.Add($"{sheet.SheetNumber} ({others.Count} other elements: {DescribeContent(others)})");
                        else
                        {
                            var number = sheet.SheetNumber;
                            doc.Delete(sheet.Id);
                            deleted.Add(number);
                        }
                    }

                    log["sheets"] = targets.Count;
                    log["sheetsCreated"] = new JArray(created);
                    log["sheetsRenamed"] = new JArray(renamed);
                    log["viewportsMoved"] = entries.Count(e => !e.Moves);
                    log["viewportsRecreated"] = entries.Count(e => e.Moves);
                    log["titlesSet"] = titles;
                    log["references"] = references;
                    log["sheetsDeleted"] = new JArray(deleted);
                    if (kept.Count > 0)
                        log["emptySheetsKept"] = new JArray(kept);
                    log["warnings"] = new JArray(warnings.Take(MaxListed));
                }
                catch (Exception ex)
                {
                    if (transaction.HasStarted())
                        transaction.RollBack();
                    return Fail($"Failed at step '{step}', nothing changed: {ex.Message}");
                }

                if (dryRun)
                {
                    transaction.RollBack();
                }
                else
                {
                    var status = transaction.Commit();
                    if (status != TransactionStatus.Committed)
                        return Fail($"Layout was not committed ({status}).");
                }
            }

            log.AddFirst(new JProperty("dryRun", dryRun));
            return Ok($"{(dryRun ? "Dry run (rolled back): " : string.Empty)}{entries.Count} viewports on {log["sheets"]} sheets, " +
                      $"{created.Count} sheets created, {((JArray)log["sheetsDeleted"]).Count} deleted.", log);
        }

        /// <summary>New sheet on the template's title block type and position, copying its writable text parameters.</summary>
        private static ViewSheet CreateSheet(Document doc, ViewSheet template, string number, string name, List<string> warnings)
        {
            var templateBlock = DetailSheetGridUtils.TitleBlockOf(doc, template);
            var sheet = ViewSheet.Create(doc, templateBlock?.GetTypeId() ?? ElementId.InvalidElementId);
            sheet.SheetNumber = number;
            if (!string.IsNullOrEmpty(name))
                sheet.Name = name;

            foreach (Parameter source in template.Parameters)
            {
                if (source.IsReadOnly || source.StorageType != StorageType.String || string.IsNullOrEmpty(source.AsString()))
                    continue;
                var builtIn = (source.Definition as InternalDefinition)?.BuiltInParameter;
                if (builtIn == BuiltInParameter.SHEET_NUMBER || builtIn == BuiltInParameter.SHEET_NAME)
                    continue;
                try
                {
                    var target = sheet.get_Parameter(source.Definition);
                    if (target != null && !target.IsReadOnly)
                        target.Set(source.AsString());
                }
                catch (Exception ex)
                {
                    warnings.Add($"{number}: parameter '{source.Definition.Name}' not copied ({ex.Message}).");
                }
            }

            // Same title block position as the template, so the measured border applies
            var newBlock = DetailSheetGridUtils.TitleBlockOf(doc, sheet);
            var from = (newBlock?.Location as LocationPoint)?.Point;
            var to = (templateBlock?.Location as LocationPoint)?.Point;
            if (newBlock != null && from != null && to != null && !from.IsAlmostEqualTo(to))
                ElementTransformUtils.MoveElement(doc, newBlock.Id, to - from);
            return sheet;
        }

        private static JObject RemapBubbles(Document doc, JObject parameters, Dictionary<string, Entry> map, List<string> warnings)
        {
            var options = DetailReferenceOptions.Read(parameters);
            var updated = 0;
            var failed = 0;
            foreach (var bubble in DetailReferenceUtils.CollectBubbles(doc, options, out _))
            {
                if (!map.TryGetValue(DetailReferenceUtils.Key(bubble.SheetNumber, bubble.DetailNumber), out var e))
                    continue;
                var sub = new SubTransaction(doc);
                sub.Start();
                try
                {
                    var instance = bubble.Instance;
                    if (!DetailReferenceUtils.SameValue(bubble.SheetNumber, e.Target.SheetNumber))
                        DetailReferenceUtils.WriteValue(instance, bubble.SheetParameter, e.Target.SheetNumber);
                    if (!DetailReferenceUtils.SameValue(bubble.DetailNumber, e.Number))
                        DetailReferenceUtils.WriteValue(instance, bubble.DetailParameter, e.Number);
                    sub.Commit();
                    updated++;
                }
                catch (Exception ex)
                {
                    if (sub.HasStarted())
                        sub.RollBack();
                    failed++;
                    warnings.Add($"bubble {bubble.Instance.Id.GetValue()}: {ex.Message}");
                }
            }

            return new JObject { ["updated"] = updated, ["failed"] = failed };
        }

        /// <summary>
        ///     Rewrites "n/SHEET" references and "DETAIL n ON THIS SHEET" (in views
        ///     that moved) through the formatted text, keeping the formatting.
        /// </summary>
        private static JObject RemapText(Document doc, string sheetPattern, Dictionary<string, Entry> map, List<Entry> entries,
            JObject references, List<string> warnings)
        {
            var regex = DetailSheetGridUtils.TextReferenceRegex(sheetPattern);
            var byView = new Dictionary<long, Entry>();
            foreach (var e in entries)
                byView[e.ViewId.GetValue()] = e;
            var updated = 0;
            var samples = new JArray();

            foreach (var note in new FilteredElementCollector(doc).OfClass(typeof(TextNote)).Cast<TextNote>().ToList())
            {
                if (!note.IsValidObject)
                    continue;
                var formatted = note.GetFormattedText();
                var text = formatted.GetPlainText();
                var edits = new List<Tuple<int, int, string>>();
                foreach (Match m in regex.Matches(text))
                {
                    if (map.TryGetValue(DetailReferenceUtils.Key(m.Groups[2].Value, m.Groups[1].Value), out var e))
                        edits.Add(Tuple.Create(m.Index, m.Length, e.Number + "/" + e.Target.SheetNumber));
                }

                if (byView.TryGetValue(note.OwnerViewId.GetValue(), out var host))
                {
                    foreach (Match m in DetailSheetGridUtils.ThisSheetRegex.Matches(text))
                    {
                        if (!map.TryGetValue(DetailReferenceUtils.Key(host.OldSheet, m.Groups[1].Value), out var e))
                            continue;
                        edits.Add(Tuple.Create(m.Index, m.Length, e.Target.Id == host.Target.Id
                            ? "DETAIL " + e.Number + " ON THIS" + m.Groups[2].Value + "SHEET"
                            : "DETAIL " + e.Number + "/" + e.Target.SheetNumber));
                    }
                }

                if (edits.Count == 0)
                    continue;
                var sub = new SubTransaction(doc);
                sub.Start();
                try
                {
                    foreach (var edit in edits.OrderByDescending(x => x.Item1))
                        formatted.SetPlainText(new TextRange(edit.Item1, edit.Item2), edit.Item3);
                    note.SetFormattedText(formatted);
                    sub.Commit();
                    updated++;
                    if (samples.Count < 20)
                    {
                        var after = note.Text.Replace("\r", " ").Replace("\n", " ");
                        samples.Add(note.Id.GetValue() + ": " + (after.Length > 80 ? after.Substring(0, 80) : after));
                    }
                }
                catch (Exception ex)
                {
                    if (sub.HasStarted())
                        sub.RollBack();
                    warnings.Add($"text {note.Id.GetValue()}: {ex.Message}");
                }
            }

            return new JObject { ["updated"] = updated, ["samples"] = samples };
        }

        /// <summary>Elements owned by the sheet other than viewports and title blocks.</summary>
        /// <summary>
        ///     Sheet-owned elements that are real content (notes, lines, clouds, schedules, ...). Not content:
        ///     viewports, the title block and anything nested in it, the title block's own revision schedule,
        ///     and uncategorised internal elements Revit keeps on every sheet.
        /// </summary>
        private static List<Element> OtherContent(Document doc, ViewSheet sheet)
        {
            return new FilteredElementCollector(doc, sheet.Id)
                .WhereElementIsNotElementType()
                .ToElements()
                .Where(e => e != null && e.Id != sheet.Id && IsSheetContent(e))
                .ToList();
        }

        private static bool IsSheetContent(Element e)
        {
            if (e is Viewport || e.Category == null || e is ViewSheet)
                return false;
            if (DocumentationUtils.IsCategory(e, BuiltInCategory.OST_TitleBlocks))
                return false;
            if (e is ScheduleSheetInstance schedule && schedule.IsTitleblockRevisionSchedule)
                return false;
            if (e is FamilyInstance instance && instance.SuperComponent != null)
                return false; // nested in the title block (or another family): goes with its host
            return true;
        }

        private static string DescribeContent(List<Element> others)
        {
            return string.Join(", ", others
                .GroupBy(e => e.Category?.Name ?? e.GetType().Name)
                .Select(g => $"{g.Count()} {g.Key}"));
        }

        // ---------------------------------------------------------------- verify

        private static AIResult<object> Verify(Document doc, JObject parameters)
        {
            var warnings = new List<string>();
            var sheets = DetailSheetGridUtils.SelectSheets(doc, parameters);
            if (sheets.Count == 0)
                return Fail("No sheets matched the selection.");
            var border = DetailSheetGridUtils.ReadBorder(doc, parameters, sheets, warnings, out _);
            var tol = DocumentationUtils.MmToFeet(0.5);

            var outside = new List<string>();
            var overlaps = new List<string>();
            var temporary = new List<string>();
            var lines = new List<string>();
            var checkedCount = 0;
            foreach (var sheet in sheets)
            {
                var rects = new List<Tuple<string, Outline>>();
                foreach (var id in sheet.GetAllViewports())
                {
                    if (!(doc.GetElement(id) is Viewport viewport))
                        continue;
                    var number = ViewportUtils.DetailNumber(viewport);
                    if (number.StartsWith(DetailSheetGridUtils.TemporaryPrefix, StringComparison.Ordinal))
                        temporary.Add($"TMP {sheet.SheetNumber} {number}");
                    rects.Add(Tuple.Create(number, viewport.GetBoxOutline()));
                    var label = DetailSheetGridUtils.IsTitled(doc, viewport) ? DetailSheetGridUtils.LabelOutline(viewport) : null;
                    if (label != null)
                        rects.Add(Tuple.Create("title " + number, label));
                }

                int sheetOut = 0, sheetOverlap = 0;
                foreach (var r in rects)
                {
                    checkedCount++;
                    var o = r.Item2;
                    if (o.MinimumPoint.X < border.X0 - tol || o.MinimumPoint.Y < border.Y0 - tol ||
                        o.MaximumPoint.X > border.X1 + tol || o.MaximumPoint.Y > border.Y1 + tol)
                    {
                        sheetOut++;
                        outside.Add($"OUT {sheet.SheetNumber} {r.Item1}");
                    }
                }

                for (var i = 0; i < rects.Count; i++)
                for (var j = i + 1; j < rects.Count; j++)
                {
                    var a = rects[i].Item2;
                    var b = rects[j].Item2;
                    if (Root(rects[i].Item1) == Root(rects[j].Item1))
                        continue;
                    if (a.MinimumPoint.X < b.MaximumPoint.X - tol && b.MinimumPoint.X < a.MaximumPoint.X - tol &&
                        a.MinimumPoint.Y < b.MaximumPoint.Y - tol && b.MinimumPoint.Y < a.MaximumPoint.Y - tol)
                    {
                        sheetOverlap++;
                        overlaps.Add($"OVL {sheet.SheetNumber} {rects[i].Item1} x {rects[j].Item1}");
                    }
                }

                lines.Add($"{sheet.SheetNumber}: {rects.Count(r => !r.Item1.StartsWith("title ", StringComparison.Ordinal))} viewports" +
                          (sheetOut + sheetOverlap == 0 ? ", ok" : $", {sheetOut} OUT, {sheetOverlap} OVL"));
            }

            // References into the selected sheets that resolve to no placed viewport
            var index = ViewportIndex.Build(doc);
            var pattern = DetailSheetGridUtils.SheetAlternation(parameters.Value<string>("textSheetPattern"), sheets.Select(s => s.SheetNumber));
            var textRegex = DetailSheetGridUtils.TextReferenceRegex(pattern);
            var sheetRegex = new Regex("^(?:" + pattern + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var badText = new List<string>();
            foreach (var note in new FilteredElementCollector(doc).OfClass(typeof(TextNote)).Cast<TextNote>())
            {
                foreach (Match m in textRegex.Matches(note.Text ?? string.Empty))
                {
                    if (index.Find(m.Groups[2].Value, m.Groups[1].Value) == null)
                        badText.Add($"BAD text {note.Id.GetValue()} {m.Value}");
                }
            }

            var options = DetailReferenceOptions.Read(parameters);
            var badBubbles = new List<string>();
            foreach (var bubble in DetailReferenceUtils.CollectBubbles(doc, options, out _))
            {
                var sheetNumber = bubble.SheetNumber.Trim();
                var detail = bubble.DetailNumber.Trim();
                // Letter-only bubbles (e.g. "B / S-000") are library sub-detail markers
                if (!sheetRegex.IsMatch(sheetNumber) || options.Placeholders.Contains(DetailReferenceUtils.Normalize(detail)) ||
                    !detail.Any(char.IsDigit))
                    continue;
                if (index.Find(sheetNumber, detail) == null)
                    badBubbles.Add($"BAD bubble {bubble.Instance.Id.GetValue()} {detail}/{sheetNumber}");
            }

            var allClear = outside.Count + overlaps.Count + temporary.Count + badText.Count + badBubbles.Count == 0;
            var response = new JObject
            {
                ["allClear"] = allClear,
                ["checked"] = checkedCount,
                ["border"] = border.ToMm(),
                ["sheets"] = new JArray(lines),
                ["counts"] = new JObject
                {
                    ["out"] = outside.Count,
                    ["overlaps"] = overlaps.Count,
                    ["temporaryNumbers"] = temporary.Count,
                    ["badText"] = badText.Count,
                    ["badBubbles"] = badBubbles.Count
                },
                ["issues"] = new JArray(outside.Concat(overlaps).Concat(temporary).Concat(badText).Concat(badBubbles).Take(MaxListed)),
                ["warnings"] = new JArray(warnings)
            };
            return Ok(allClear
                ? $"all clear: {checkedCount} boxes/titles on {sheets.Count} sheets"
                : $"{outside.Count} OUT, {overlaps.Count} OVL, {temporary.Count} TMP, {badText.Count + badBubbles.Count} BAD on {sheets.Count} sheets", response);
        }

        /// <summary>Composite parts n, nA, nB and their titles share the root n.</summary>
        private static string Root(string label)
        {
            var number = label.StartsWith("title ", StringComparison.Ordinal) ? label.Substring(6) : label;
            var digits = new string(number.TakeWhile(char.IsDigit).ToArray());
            return digits.Length > 0 ? digits : number;
        }
    }
}
