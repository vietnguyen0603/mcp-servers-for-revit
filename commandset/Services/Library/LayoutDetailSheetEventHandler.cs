using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Library
{
    /// <summary>
    ///     Lays views out on a detail sheet in a module grid. Each view is
    ///     placed (or its existing viewport on the sheet reused), measured with
    ///     its box and label outlines, then packed first-fit into grid cells in
    ///     reading order; large details span several cells. Viewports are moved
    ///     with SetBoxCenter and numbered sequentially in grid order.
    /// </summary>
    public class LayoutDetailSheetEventHandler : JsonParameterEventHandler
    {
        private const double DefaultMarginMm = 20;
        private const double DefaultRightMarginMm = 110;
        private const double DefaultGapMm = 10;
        private const int MaxAutoColumns = 8;
        private const int MaxAutoRows = 6;

        public override string GetName() => "Layout Detail Sheet";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var sheet = DocumentationUtils.GetElement<ViewSheet>(doc, DocumentationUtils.ReadId(parameters, "sheetId"))
                        ?? throw new ArgumentException("'sheetId' does not refer to a sheet.");
            var viewIds = DocumentationUtils.RequireArray(parameters, "viewIds");
            var dryRun = parameters.Value<bool?>("dryRun") ?? false;
            var rightToLeft = !string.Equals(parameters.Value<string>("order") ?? "rightToLeftTopDown",
                "leftToRightTopDown", StringComparison.OrdinalIgnoreCase);
            var columns = parameters.Value<int?>("columns");
            var rows = parameters.Value<int?>("rows");
            var gap = DocumentationUtils.MmToFeet(Math.Max(0, parameters.Value<double?>("gap") ?? DefaultGapMm));
            var startNumber = parameters.Value<int?>("startDetailNumber") ?? 1;
            var numbering = parameters.Value<bool?>("numbering") ?? true;
            var viewportTypeId = DocumentationUtils.ReadId(parameters, "viewportTypeId");

            var region = ReadRegion(doc, sheet, parameters);
            if (region.Width <= 0 || region.Height <= 0)
                throw new ArgumentException("The layout region is empty.");

            var items = new List<LayoutItem>();
            var errors = new List<object>();
            object response;
            using (var transaction = DocumentationUtils.StartTransaction(doc, "MCP: Layout Detail Sheet"))
            {
                for (var i = 0; i < viewIds.Count; i++)
                {
                    var sub = new SubTransaction(doc);
                    sub.Start();
                    try
                    {
                        items.Add(Place(doc, sheet, viewIds[i].Value<long>(), region, viewportTypeId, i));
                        sub.Commit();
                    }
                    catch (Exception ex)
                    {
                        if (sub.HasStarted())
                            sub.RollBack();
                        errors.Add(new { index = i, viewId = viewIds[i].Value<long>(), message = ex.Message });
                    }
                }

                doc.Regenerate();
                foreach (var item in items)
                    Measure(item);

                var grid = ChooseGrid(items, region, gap, columns, rows, rightToLeft);
                var placed = items.Where(i => i.Column >= 0).OrderBy(i => i.ScanIndex).ToList();
                foreach (var item in items.Where(i => i.Column < 0))
                {
                    errors.Add(new { index = item.Index, viewId = item.View.Id.GetValue(), message = "Does not fit the layout region." });
                    if (item.Created)
                        doc.Delete(item.Viewport.Id);
                }

                foreach (var item in placed)
                    Position(item, grid, region);

                if (numbering)
                    Number(doc, sheet, placed, startNumber, errors);

                doc.Regenerate();
                response = new
                {
                    dryRun,
                    sheetId = sheet.Id.GetValue(),
                    sheetNumber = sheet.SheetNumber,
                    region = new
                    {
                        min = new { x = DocumentationUtils.FeetToMm(region.MinX), y = DocumentationUtils.FeetToMm(region.MinY) },
                        max = new { x = DocumentationUtils.FeetToMm(region.MaxX), y = DocumentationUtils.FeetToMm(region.MaxY) }
                    },
                    grid = new
                    {
                        columns = grid.Columns,
                        rows = grid.Rows,
                        cellWidth = DocumentationUtils.FeetToMm(grid.CellWidth),
                        cellHeight = DocumentationUtils.FeetToMm(grid.CellHeight)
                    },
                    placed = placed.Count,
                    failed = errors.Count,
                    errors,
                    placements = placed.Select(i => new
                    {
                        viewId = i.View.Id.GetValue(),
                        viewName = i.View.Name,
                        viewportId = dryRun && i.Created ? (long?)null : i.Viewport.Id.GetValue(),
                        newViewport = i.Created,
                        detailNumber = DetailReferenceUtils.ViewportDetailNumber(i.Viewport),
                        column = i.Column + 1,
                        row = i.Row + 1,
                        spanColumns = i.SpanX,
                        spanRows = i.SpanY,
                        center = DocumentationUtils.PointToMm(i.Viewport.GetBoxCenter()),
                        width = DocumentationUtils.FeetToMm(i.Width),
                        height = DocumentationUtils.FeetToMm(i.Height)
                    }).ToList()
                };

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

            return Ok($"{(dryRun ? "Dry run: " : string.Empty)}laid out {items.Count(i => i.Column >= 0)} of {viewIds.Count} views on {sheet.SheetNumber}.",
                response);
        }

        private static Region ReadRegion(Document doc, ViewSheet sheet, JObject parameters)
        {
            if (parameters["region"] is JObject explicitRegion)
            {
                var min = DocumentationUtils.ReadPointMm(explicitRegion["min"])
                          ?? throw new ArgumentException("'region.min' is required (mm).");
                var max = DocumentationUtils.ReadPointMm(explicitRegion["max"])
                          ?? throw new ArgumentException("'region.max' is required (mm).");
                return new Region(Math.Min(min.X, max.X), Math.Min(min.Y, max.Y), Math.Max(min.X, max.X), Math.Max(min.Y, max.Y));
            }

            double minX, minY, maxX, maxY;
            var titleBlock = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstOrDefault();
            var box = titleBlock?.get_BoundingBox(sheet);
            if (box != null)
            {
                minX = box.Min.X;
                minY = box.Min.Y;
                maxX = box.Max.X;
                maxY = box.Max.Y;
            }
            else
            {
                minX = sheet.Outline.Min.U;
                minY = sheet.Outline.Min.V;
                maxX = sheet.Outline.Max.U;
                maxY = sheet.Outline.Max.V;
            }

            var margins = parameters["margins"] as JObject;
            double Margin(string side, double fallback) =>
                DocumentationUtils.MmToFeet(margins?.Value<double?>(side) ?? fallback);

            return new Region(
                minX + Margin("left", DefaultMarginMm),
                minY + Margin("bottom", DefaultMarginMm),
                maxX - Margin("right", DefaultRightMarginMm),
                maxY - Margin("top", DefaultMarginMm));
        }

        private static LayoutItem Place(Document doc, ViewSheet sheet, long viewId, Region region, long? viewportTypeId,
            int index)
        {
            var view = doc.GetElement(viewId.ToRevitElementId()) as View
                       ?? throw new ArgumentException($"{viewId} is not a view.");
            if (view.IsTemplate || view is ViewSchedule || view is ViewSheet)
                throw new ArgumentException($"'{view.Name}' cannot be laid out as a viewport.");

            var existing = sheet.GetAllViewports()
                .Select(id => doc.GetElement(id) as Viewport)
                .FirstOrDefault(vp => vp != null && vp.ViewId == view.Id);
            var created = false;
            if (existing == null)
            {
                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                    throw new InvalidOperationException(
                        $"'{view.Name}' cannot be added to sheet {sheet.SheetNumber}; it may already be on another sheet.");
                existing = Viewport.Create(doc, sheet.Id, view.Id,
                    new XYZ((region.MinX + region.MaxX) / 2, (region.MinY + region.MaxY) / 2, 0));
                created = true;
            }

            if (viewportTypeId != null && existing.GetTypeId().GetValue() != viewportTypeId.Value)
                existing.ChangeTypeId(viewportTypeId.Value.ToRevitElementId());

            return new LayoutItem { Index = index, View = view, Viewport = existing, Created = created };
        }

        /// <summary>Extent = box outline united with the title label outline.</summary>
        private static void Measure(LayoutItem item)
        {
            var box = item.Viewport.GetBoxOutline();
            double minX = box.MinimumPoint.X, minY = box.MinimumPoint.Y;
            double maxX = box.MaximumPoint.X, maxY = box.MaximumPoint.Y;
            try
            {
                var label = item.Viewport.GetLabelOutline();
                if (label != null && !label.IsEmpty)
                {
                    minX = Math.Min(minX, label.MinimumPoint.X);
                    minY = Math.Min(minY, label.MinimumPoint.Y);
                    maxX = Math.Max(maxX, label.MaximumPoint.X);
                    maxY = Math.Max(maxY, label.MaximumPoint.Y);
                }
            }
            catch (Exception)
            {
                // No visible title: the box outline is the whole extent.
            }

            var boxCenter = item.Viewport.GetBoxCenter();
            item.Width = maxX - minX;
            item.Height = maxY - minY;
            item.OffsetX = boxCenter.X - (minX + maxX) / 2;
            item.OffsetY = boxCenter.Y - (minY + maxY) / 2;
        }

        /// <summary>
        ///     Uses the requested grid, or tries every grid up to 8x6 and keeps
        ///     the coarsest one that fits all views (ties: more columns).
        /// </summary>
        private static Grid ChooseGrid(List<LayoutItem> items, Region region, double gap, int? columns, int? rows,
            bool rightToLeft)
        {
            var columnRange = columns is int c ? new[] { Math.Max(1, c) } : Enumerable.Range(1, MaxAutoColumns).ToArray();
            var rowRange = rows is int r ? new[] { Math.Max(1, r) } : Enumerable.Range(1, MaxAutoRows).ToArray();
            var candidates = columnRange
                .SelectMany(cc => rowRange.Select(rr => new Grid(cc, rr, region)))
                .OrderBy(g => g.Columns * g.Rows)
                .ThenByDescending(g => g.Columns)
                .ToList();

            foreach (var grid in candidates)
            {
                if (Pack(items, grid, gap, rightToLeft) == items.Count)
                    return grid;
            }

            // Nothing fits everything: keep the grid that places the most views.
            var best = candidates.OrderByDescending(g => Pack(items, g, gap, rightToLeft)).ThenBy(g => g.Columns * g.Rows).First();
            Pack(items, best, gap, rightToLeft);
            return best;
        }

        /// <summary>First-fit packing in scan order; returns the number of views placed.</summary>
        private static int Pack(List<LayoutItem> items, Grid grid, double gap, bool rightToLeft)
        {
            var used = new bool[grid.Rows, grid.Columns];
            var placed = 0;
            foreach (var item in items)
            {
                item.Column = item.Row = -1;
                item.SpanX = Math.Max(1, (int)Math.Ceiling((item.Width + gap) / grid.CellWidth - 1e-9));
                item.SpanY = Math.Max(1, (int)Math.Ceiling((item.Height + gap) / grid.CellHeight - 1e-9));
                if (item.SpanX > grid.Columns || item.SpanY > grid.Rows)
                    continue;

                for (var scan = 0; scan < grid.Rows * grid.Columns && item.Column < 0; scan++)
                {
                    var row = scan / grid.Columns;
                    var step = scan % grid.Columns;
                    // Anchor is the block's top-right cell (right-to-left) or top-left cell.
                    var left = rightToLeft ? grid.Columns - 1 - step - (item.SpanX - 1) : step;
                    if (left < 0 || left + item.SpanX > grid.Columns || row + item.SpanY > grid.Rows)
                        continue;
                    if (!IsFree(used, row, left, item.SpanY, item.SpanX))
                        continue;

                    for (var y = row; y < row + item.SpanY; y++)
                    for (var x = left; x < left + item.SpanX; x++)
                        used[y, x] = true;
                    item.Row = row;
                    item.Column = left;
                    item.ScanIndex = scan;
                    placed++;
                }
            }

            return placed;
        }

        private static bool IsFree(bool[,] used, int row, int column, int spanY, int spanX)
        {
            for (var y = row; y < row + spanY; y++)
            for (var x = column; x < column + spanX; x++)
                if (used[y, x])
                    return false;
            return true;
        }

        /// <summary>Centres the view's extent in its block of cells.</summary>
        private static void Position(LayoutItem item, Grid grid, Region region)
        {
            var blockMinX = region.MinX + item.Column * grid.CellWidth;
            var blockMaxY = region.MaxY - item.Row * grid.CellHeight;
            var centerX = blockMinX + item.SpanX * grid.CellWidth / 2;
            var centerY = blockMaxY - item.SpanY * grid.CellHeight / 2;
            item.Viewport.SetBoxCenter(new XYZ(centerX + item.OffsetX, centerY + item.OffsetY, 0));
        }

        /// <summary>
        ///     Numbers viewports in grid order from <paramref name="start" />,
        ///     skipping numbers held by other viewports on the sheet. Temporary
        ///     numbers are written first so swaps within the set never collide.
        /// </summary>
        private static void Number(Document doc, ViewSheet sheet, List<LayoutItem> placed, int start, List<object> errors)
        {
            var ours = new HashSet<long>(placed.Select(i => i.Viewport.Id.GetValue()));
            var taken = new HashSet<string>(sheet.GetAllViewports()
                .Where(id => !ours.Contains(id.GetValue()))
                .Select(id => doc.GetElement(id) as Viewport)
                .Where(vp => vp != null)
                .Select(vp => DetailReferenceUtils.Normalize(DetailReferenceUtils.ViewportDetailNumber(vp))));

            var original = placed.ToDictionary(i => i, i => DetailReferenceUtils.ViewportDetailNumber(i.Viewport));
            foreach (var item in placed)
                item.Viewport.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.Set("~mcp" + item.Viewport.Id.GetValue());

            var next = start;
            foreach (var item in placed)
            {
                while (taken.Contains(next.ToString()))
                    next++;
                var parameter = item.Viewport.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER);
                try
                {
                    if (parameter == null || !parameter.Set(next.ToString()))
                        throw new InvalidOperationException("Detail Number could not be set.");
                }
                catch (Exception ex)
                {
                    errors.Add(new { viewId = item.View.Id.GetValue(), message = $"Detail number {next}: {ex.Message}" });
                    try
                    {
                        parameter?.Set(original[item]);
                    }
                    catch (Exception)
                    {
                        // Keep the temporary number; reported above.
                    }
                }

                next++;
            }
        }

        private sealed class LayoutItem
        {
            public int Index { get; set; }
            public View View { get; set; }
            public Viewport Viewport { get; set; }
            public bool Created { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public double OffsetX { get; set; }
            public double OffsetY { get; set; }
            public int Column { get; set; } = -1;
            public int Row { get; set; } = -1;
            public int SpanX { get; set; } = 1;
            public int SpanY { get; set; } = 1;
            public int ScanIndex { get; set; }
        }

        private sealed class Region
        {
            public Region(double minX, double minY, double maxX, double maxY)
            {
                MinX = minX;
                MinY = minY;
                MaxX = maxX;
                MaxY = maxY;
            }

            public double MinX { get; }
            public double MinY { get; }
            public double MaxX { get; }
            public double MaxY { get; }
            public double Width => MaxX - MinX;
            public double Height => MaxY - MinY;
        }

        private sealed class Grid
        {
            public Grid(int columns, int rows, Region region)
            {
                Columns = columns;
                Rows = rows;
                CellWidth = region.Width / columns;
                CellHeight = region.Height / rows;
            }

            public int Columns { get; }
            public int Rows { get; }
            public double CellWidth { get; }
            public double CellHeight { get; }
        }
    }
}
