using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services.Library;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Edits placed viewports in one transaction: viewport type, box centre,
    ///     title offset/line length (Revit 2022+), anchoring a view point to a
    ///     sheet point, and detail numbers. Per-item changes run first, each in
    ///     its own sub-transaction; detail numbers are then applied for the
    ///     whole batch through unique temporary numbers so swaps never collide.
    /// </summary>
    public class UpdateViewportsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Update Viewports";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "viewports");
            var states = new List<ItemState>();
            var seen = new HashSet<long>();

            using (var transaction = DocumentationUtils.StartTransaction(doc, "MCP: Update Viewports"))
            {
                for (var index = 0; index < items.Count; index++)
                {
                    var state = new ItemState { Index = index };
                    states.Add(state);
                    var subTransaction = new SubTransaction(doc);
                    subTransaction.Start();
                    try
                    {
                        Apply(doc, items[index], state, seen);
                        subTransaction.Commit();
                    }
                    catch (Exception ex)
                    {
                        if (subTransaction.HasStarted())
                            subTransaction.RollBack();
                        state.Error = ex.Message;
                        state.Number = null;
                    }
                }

                Renumber(doc, states.Where(s => s.Error == null && s.Number != null).ToList());

                var status = transaction.Commit();
                if (status != TransactionStatus.Committed)
                {
                    foreach (var state in states)
                        state.Error = $"Transaction was not committed ({status}).";
                }
            }

            var results = states.Select(Describe).ToList();
            return Ok($"Updated {results.Count(r => r.Value<bool>("success"))} of {results.Count} viewports.",
                DocumentationUtils.Summarize(results));
        }

        private static void Apply(Document doc, JToken item, ItemState state, ISet<long> seen)
        {
            var viewport = DocumentationUtils.GetElement<Viewport>(doc, DocumentationUtils.ReadId(item, "viewportId"))
                           ?? throw new ArgumentException("'viewportId' does not refer to a viewport.");
            if (!seen.Add(viewport.Id.GetValue()))
                throw new ArgumentException($"Viewport {viewport.Id.GetValue()} is listed more than once.");
            state.Viewport = viewport;

            var number = item["detailNumber"]?.Type == JTokenType.String ? item.Value<string>("detailNumber").Trim() : null;
            if (item["detailNumber"] != null && item["detailNumber"].Type != JTokenType.Null && string.IsNullOrEmpty(number))
                throw new ArgumentException("'detailNumber' must be a non-empty string.");

            var typeId = ViewportUtils.ResolveType(doc, viewport, DocumentationUtils.ReadId(item, "viewportTypeId"),
                item.Value<string>("viewportTypeName"));
            if (typeId != null && viewport.GetTypeId() != typeId)
                viewport.ChangeTypeId(typeId);

            var center = ViewportUtils.ReadPoint2(item["center"], "center");
            if (center != null)
                viewport.SetBoxCenter(center);

            var labelOffset = ViewportUtils.ReadPoint2(item["labelOffset"], "labelOffset");
            var labelLineLength = item.Value<double?>("labelLineLength");
            if (labelOffset != null || labelLineLength != null)
            {
#if REVIT2022_OR_GREATER
                if (labelOffset != null)
                    viewport.LabelOffset = labelOffset;
                if (labelLineLength != null)
                {
                    if (labelLineLength < 0)
                        throw new ArgumentException("'labelLineLength' must not be negative.");
                    viewport.LabelLineLength = DocumentationUtils.MmToFeet(labelLineLength.Value);
                }
#else
                throw new NotSupportedException("labelOffset/labelLineLength require Revit 2022 or later.");
#endif
            }

            if (ViewportUtils.TryReadAnchor(item, out var viewPoint, out var sheetPoint))
            {
                doc.Regenerate();
                state.Anchor = ViewportUtils.Anchor(doc, viewport, viewPoint, sheetPoint);
            }

            state.Number = number;
        }

        /// <summary>
        ///     Applies the requested detail numbers: rejects numbers requested twice
        ///     on one sheet or held by a viewport outside the renumbered set, writes
        ///     a unique temporary number to every remaining viewport, then the finals.
        /// </summary>
        private static void Renumber(Document doc, List<ItemState> requested)
        {
            if (requested.Count == 0)
                return;

            foreach (var group in requested.GroupBy(s =>
                         s.Viewport.SheetId.GetValue() + "\u0001" + DetailReferenceUtils.Normalize(s.Number)))
            {
                if (group.Count() < 2)
                    continue;
                foreach (var state in group)
                    state.NumberError = $"Detail number '{state.Number}' is requested for more than one viewport on the same sheet.";
            }

            var batch = requested.Where(s => s.NumberError == null).ToList();
            var renumbered = new HashSet<long>(batch.Select(s => s.Viewport.Id.GetValue()));
            foreach (var state in batch)
            {
                var holder = ViewportUtils.FindHolder(doc, state.Viewport.SheetId, state.Number, renumbered);
                if (holder != null)
                    state.NumberError =
                        $"Detail number '{state.Number}' is used by viewport {holder.Id.GetValue()} " +
                        $"('{DocumentationUtils.ViewName(doc, holder.ViewId)}'), which is not renumbered in this call.";
            }

            batch = batch.Where(s => s.NumberError == null).ToList();
            var original = batch.ToDictionary(s => s, s => ViewportUtils.DetailNumber(s.Viewport));

            foreach (var state in batch)
            {
                try
                {
                    ViewportUtils.WriteDetailNumber(state.Viewport, "~mcp" + state.Viewport.Id.GetValue());
                }
                catch (Exception ex)
                {
                    state.NumberError = ex.Message;
                }
            }

            foreach (var state in batch.Where(s => s.NumberError == null))
            {
                try
                {
                    ViewportUtils.WriteDetailNumber(state.Viewport, state.Number);
                }
                catch (Exception ex)
                {
                    state.NumberError = ex.Message;
                    try
                    {
                        ViewportUtils.WriteDetailNumber(state.Viewport, original[state]);
                    }
                    catch (Exception)
                    {
                        // Keeps the temporary number; reported in the item error.
                        state.NumberError += " The viewport kept a temporary number.";
                    }
                }
            }
        }

        private static JObject Describe(ItemState state)
        {
            var result = new JObject { ["index"] = state.Index };
            var error = state.Error ?? (state.NumberError == null
                ? null
                : $"Detail number: {state.NumberError} Other requested changes to this viewport were applied.");
            result["success"] = error == null;
            if (error != null)
                result["message"] = error;

            var viewport = state.Error == null ? state.Viewport : null;
            if (viewport == null)
            {
                if (state.Viewport != null)
                    result["viewportId"] = state.Viewport.Id.GetValue();
                return result;
            }

            var doc = viewport.Document;
            var center = viewport.GetBoxCenter();
            result["viewportId"] = viewport.Id.GetValue();
            result["sheetId"] = viewport.SheetId.GetValue();
            result["viewId"] = viewport.ViewId.GetValue();
            result["detailNumber"] = ViewportUtils.DetailNumber(viewport);
            result["viewportTypeId"] = viewport.GetTypeId().GetValue();
            result["viewportTypeName"] = doc.GetElement(viewport.GetTypeId())?.Name;
            result["center"] = new JObject
            {
                ["x"] = DocumentationUtils.FeetToMm(center.X),
                ["y"] = DocumentationUtils.FeetToMm(center.Y)
            };
#if REVIT2022_OR_GREATER
            result["labelOffset"] = new JObject
            {
                ["x"] = DocumentationUtils.FeetToMm(viewport.LabelOffset.X),
                ["y"] = DocumentationUtils.FeetToMm(viewport.LabelOffset.Y)
            };
            result["labelLineLength"] = DocumentationUtils.FeetToMm(viewport.LabelLineLength);
#endif
            if (state.Anchor != null)
                result["anchor"] = state.Anchor;
            return result;
        }

        private sealed class ItemState
        {
            public int Index;
            public Viewport Viewport;
            public string Number;
            public JObject Anchor;
            public string Error;
            public string NumberError;
        }
    }
}
