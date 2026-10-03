using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Updates existing sheets: renumber, rename, write parameter values
    ///     (sheet first, then its title block) and add or remove additional
    ///     revisions. Revisions that come from clouds on the sheet cannot be
    ///     removed and are reported as warnings.
    /// </summary>
    public class UpdateSheetsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Update Sheets";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "sheets");
            var results = DocumentationUtils.RunBatch(doc, "MCP: Update Sheets", items, item => Update(doc, (JObject)item));
            return Ok($"Updated {results.Count(r => r.Value<bool>("success"))} of {results.Count} sheets.",
                DocumentationUtils.Summarize(results));
        }

        private static object Update(Document doc, JObject item)
        {
            var sheet = FindSheet(doc, item);
            var warnings = new List<string>();

            var newNumber = item.Value<string>("newNumber");
            if (!string.IsNullOrWhiteSpace(newNumber) && newNumber.Trim() != sheet.SheetNumber)
            {
                var trimmed = newNumber.Trim();
                var clash = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Any(s => s.Id != sheet.Id && string.Equals(s.SheetNumber, trimmed, StringComparison.OrdinalIgnoreCase));
                if (clash)
                    throw new InvalidOperationException($"Sheet number '{trimmed}' is already in use.");
                sheet.SheetNumber = trimmed;
            }

            var newName = item.Value<string>("newName");
            if (!string.IsNullOrWhiteSpace(newName))
                sheet.Name = newName.Trim();

            if (item["parameters"] is JObject values && values.Count > 0)
            {
                var titleBlock = new FilteredElementCollector(doc, sheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType()
                    .FirstElement();

                foreach (var property in values.Properties())
                {
                    var error = DocumentationUtils.SetParameterValue(sheet, property.Name, property.Value);
                    if (error != null && titleBlock != null && titleBlock.LookupParameter(property.Name) != null)
                        error = DocumentationUtils.SetParameterValue(titleBlock, property.Name, property.Value);
                    if (error != null)
                        warnings.Add(error);
                }
            }

            var add = ReadIds(item, "addRevisionIds");
            var remove = ReadIds(item, "removeRevisionIds");
            if (add.Count > 0 || remove.Count > 0)
            {
                var invalid = add.Concat(remove).Where(id => !(doc.GetElement(id.ToRevitElementId()) is Revision)).Distinct().ToList();
                if (invalid.Count > 0)
                    throw new ArgumentException($"Not revisions: {string.Join(", ", invalid)}.");

                var additional = sheet.GetAdditionalRevisionIds().Select(id => id.GetValue()).ToList();
                var fromClouds = sheet.GetAllRevisionIds().Select(id => id.GetValue())
                    .Where(id => !additional.Contains(id)).ToHashSet();

                foreach (var id in remove)
                {
                    if (additional.Remove(id))
                        continue;
                    warnings.Add(fromClouds.Contains(id)
                        ? $"Revision {id} comes from a revision cloud on the sheet and cannot be removed."
                        : $"Revision {id} is not on the sheet.");
                }

                foreach (var id in add.Where(id => !additional.Contains(id)))
                    additional.Add(id);

                sheet.SetAdditionalRevisionIds(additional.Select(id => id.ToRevitElementId()).ToList());
            }

            return new
            {
                sheetId = sheet.Id.GetValue(),
                number = sheet.SheetNumber,
                name = sheet.Name,
                revisionIds = sheet.GetAllRevisionIds().Select(id => id.GetValue()).ToList(),
                warnings = warnings.Count > 0 ? warnings : null
            };
        }

        private static ViewSheet FindSheet(Document doc, JObject item)
        {
            var sheetId = DocumentationUtils.ReadId(item, "sheetId");
            if (sheetId != null)
                return DocumentationUtils.GetElement<ViewSheet>(doc, sheetId)
                       ?? throw new ArgumentException($"sheetId {sheetId} is not a sheet.");

            var number = item.Value<string>("sheetNumber");
            if (string.IsNullOrWhiteSpace(number))
                throw new ArgumentException("Provide 'sheetId' or 'sheetNumber'.");

            return new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                       .FirstOrDefault(s => string.Equals(s.SheetNumber, number.Trim(), StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"Sheet '{number}' not found.");
        }

        private static List<long> ReadIds(JObject item, string name)
        {
            return item[name] is JArray array
                ? array.Select(t => t.Value<long>()).Distinct().ToList()
                : new List<long>();
        }
    }
}
