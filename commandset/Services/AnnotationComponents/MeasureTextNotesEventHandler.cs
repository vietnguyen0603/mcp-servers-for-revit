using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Measures text notes (paper/model size, bounding box, box corners, width
    ///     setting, line count, wrapping). Given elementIds, or every text note of
    ///     viewId (default active view). Read-only: the unwrapped measurement
    ///     creates temporary notes inside a transaction that is always rolled back.
    /// </summary>
    public class MeasureTextNotesEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Measure Text Notes";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var viewId = DocumentationUtils.ReadId(parameters, "viewId");
            var requested = parameters["elementIds"]?.ToObject<List<long>>();

            var notes = new List<TextNote>();
            var errors = new List<object>();
            View view = null;
            if (requested != null && requested.Count > 0)
            {
                foreach (var id in requested.Distinct())
                {
                    var note = doc.GetElement(id.ToRevitElementId()) as TextNote;
                    if (note == null)
                        errors.Add(new { elementId = id, message = "Not a text note." });
                    else if (viewId != null && note.OwnerViewId.GetValue() != viewId)
                        errors.Add(new { elementId = id, message = $"Not in view {viewId}." });
                    else
                        notes.Add(note);
                }
            }
            else
            {
                view = viewId != null
                    ? DocumentationUtils.GetElement<View>(doc, viewId) ?? throw new ArgumentException($"viewId {viewId} is not a view.")
                    : uiDoc.ActiveView;
                var limit = Math.Max(1, Math.Min(1000, parameters.Value<int?>("limit") ?? 500));
                notes = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(TextNote))
                    .Cast<TextNote>()
                    .Where(n => n.OwnerViewId == view.Id)
                    .OrderBy(n => n.Id.GetValue())
                    .Take(limit)
                    .ToList();
            }

            var results = new List<object>();
            var wrapAnalysis = true;
            Transaction transaction = null;
            try
            {
                try
                {
                    transaction = DocumentationUtils.StartTransaction(doc, "MCP: Measure Text Notes");
                }
                catch (Exception)
                {
                    // Read-only document or open transaction elsewhere: measure without the wrap analysis.
                    transaction = null;
                    wrapAnalysis = false;
                }

                foreach (var note in notes)
                {
                    try
                    {
                        TextNoteMeasure.Unwrapped? unwrapped = null;
                        if (wrapAnalysis)
                            unwrapped = TextNoteMeasure.MeasureUnwrapped(doc, note);
                        results.Add(TextNoteMeasure.Describe(doc, note, unwrapped));
                    }
                    catch (Exception ex)
                    {
                        errors.Add(new { elementId = note.Id.GetValue(), message = ex.Message });
                    }
                }
            }
            finally
            {
                if (transaction != null && transaction.HasStarted() && !transaction.HasEnded())
                    transaction.RollBack();
                transaction?.Dispose();
            }

            var scope = view != null ? $" in '{view.Name}'" : string.Empty;
            return Ok($"Measured {results.Count} text notes{scope}" + (errors.Count > 0 ? $"; {errors.Count} failed." : "."),
                new
                {
                    viewId = view?.Id.GetValue(),
                    measured = results.Count,
                    wrapAnalysis,
                    notes = results,
                    errors = errors.Count > 0 ? errors : null
                });
        }
    }
}
