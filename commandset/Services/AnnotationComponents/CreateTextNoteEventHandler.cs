using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Creates text notes. Locations are model millimetres for model views
    ///     and sheet millimetres for sheets; width is paper-space millimetres.
    ///     Optional leaders (end/elbow points in the same millimetre coordinates)
    ///     and whole-note formatting are applied after creation.
    /// </summary>
    public class CreateTextNoteEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Create Text Note";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "notes");
            var results = DocumentationUtils.RunBatch(doc, "MCP: Create Text Notes", items, item =>
            {
                var text = item.Value<string>("text");
                if (string.IsNullOrEmpty(text))
                    throw new ArgumentException("'text' is required.");
                var location = DocumentationUtils.ReadPointMm(item["location"])
                               ?? throw new ArgumentException("'location' is required (mm).");

                var view = DocumentationUtils.GetElement<View>(doc, DocumentationUtils.ReadId(item, "viewId"))
                           ?? uiDoc.ActiveView;
                if (view.IsTemplate)
                    throw new ArgumentException("Text notes cannot be placed in a view template.");

                var typeId = DocumentationUtils.ReadId(item, "textNoteTypeId") is long requested
                    ? DocumentationUtils.GetElement<TextNoteType>(doc, requested)?.Id
                      ?? throw new ArgumentException($"textNoteTypeId {requested} is not a text note type.")
                    : doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);

                var options = new TextNoteOptions(typeId)
                {
                    HorizontalAlignment = DocumentationUtils.ParseEnum(item.Value<string>("horizontalAlignment"),
                        HorizontalTextAlignment.Left),
                    Rotation = (item.Value<double?>("rotationDegrees") ?? 0) * Math.PI / 180
                };

                var width = item.Value<double?>("width");
                var note = width is double w && w > 0
                    ? TextNote.Create(doc, view.Id, location, DocumentationUtils.MmToFeet(w), text, options)
                    : TextNote.Create(doc, view.Id, location, text, options);

                TextNoteStyling.ApplyFormat(note, item["format"]);
                TextNoteStyling.ApplyAttachments(note, item);
                TextNoteStyling.AddLeaders(doc, note, view, item["leaders"]);

                return new
                {
                    textNoteId = note.Id.GetValue(),
                    viewId = view.Id.GetValue(),
                    leaderCount = note.LeaderCount
                };
            });

            return Ok($"Created {results.Count(r => r.Value<bool>("success"))} of {results.Count} text notes.",
                DocumentationUtils.Summarize(results));
        }
    }
}
