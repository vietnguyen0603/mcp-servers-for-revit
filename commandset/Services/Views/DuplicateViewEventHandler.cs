using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Duplicates views as plain copies, copies with detailing, or
    ///     dependent views, optionally renaming each copy.
    /// </summary>
    public class DuplicateViewEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Duplicate View";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "views");
            var results = DocumentationUtils.RunBatch(doc, "MCP: Duplicate Views", items, item =>
            {
                var source = DocumentationUtils.GetElement<View>(doc, DocumentationUtils.ReadId(item, "viewId"))
                             ?? throw new ArgumentException("'viewId' does not refer to a view.");
                var option = DocumentationUtils.ParseEnum(item.Value<string>("mode"), ViewDuplicateOption.Duplicate);
                if (!source.CanViewBeDuplicated(option))
                    throw new InvalidOperationException($"View '{source.Name}' cannot be duplicated with mode {option}.");

                var copy = (View)doc.GetElement(source.Duplicate(option));
                var name = item.Value<string>("name");
                if (!string.IsNullOrWhiteSpace(name))
                    copy.Name = name.Trim();

                return new { sourceViewId = source.Id.GetValue(), viewId = copy.Id.GetValue(), name = copy.Name };
            });

            return Ok($"Duplicated {results.Count(r => r.Value<bool>("success"))} of {results.Count} views.",
                DocumentationUtils.Summarize(results));
        }
    }
}
