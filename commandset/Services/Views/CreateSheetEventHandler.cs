using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Creates sheets with a title block, number, name, parameter values
    ///     (written to the sheet, falling back to its title block) and
    ///     additional revisions.
    /// </summary>
    public class CreateSheetEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Create Sheet";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "sheets");
            var results = DocumentationUtils.RunBatch(doc, "MCP: Create Sheets", items, item => CreateSheet(doc, (JObject)item));
            return Ok($"Created {results.Count(r => r.Value<bool>("success"))} of {results.Count} sheets.",
                DocumentationUtils.Summarize(results));
        }

        private static object CreateSheet(Document doc, JObject item)
        {
            var titleBlockId = item.Value<bool?>("noTitleBlock") == true
                ? ElementId.InvalidElementId
                : ResolveTitleBlock(doc, item);

            var sheet = ViewSheet.Create(doc, titleBlockId);

            var number = item.Value<string>("number");
            if (!string.IsNullOrWhiteSpace(number))
                sheet.SheetNumber = number.Trim();
            var name = item.Value<string>("name");
            if (!string.IsNullOrWhiteSpace(name))
                sheet.Name = name.Trim();

            var warnings = new List<string>();
            if (item["parameters"] is JObject values && values.Count > 0)
            {
                doc.Regenerate();
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

            if (item["revisionIds"] is JArray revisionTokens && revisionTokens.Count > 0)
            {
                var revisionIds = revisionTokens.Select(t => t.Value<long>().ToRevitElementId()).ToList();
                var invalid = revisionIds.Where(id => !(doc.GetElement(id) is Revision)).ToList();
                if (invalid.Count > 0)
                    throw new ArgumentException($"Not revisions: {string.Join(", ", invalid.Select(id => id.GetValue()))}.");
                sheet.SetAdditionalRevisionIds(revisionIds);
            }

            return new
            {
                sheetId = sheet.Id.GetValue(),
                number = sheet.SheetNumber,
                name = sheet.Name,
                titleBlockTypeId = titleBlockId == ElementId.InvalidElementId ? (long?)null : titleBlockId.GetValue(),
                warnings = warnings.Count > 0 ? warnings : null
            };
        }

        private static ElementId ResolveTitleBlock(Document doc, JObject item)
        {
            var titleBlockTypes = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .Cast<FamilySymbol>()
                .OrderBy(t => t.FamilyName)
                .ThenBy(t => t.Name)
                .ToList();

            var typeId = DocumentationUtils.ReadId(item, "titleBlockTypeId");
            if (typeId != null)
            {
                return titleBlockTypes.FirstOrDefault(t => t.Id.GetValue() == typeId)?.Id
                       ?? throw new ArgumentException($"titleBlockTypeId {typeId} is not a title block type.");
            }

            var familyName = item.Value<string>("titleBlockFamilyName");
            var typeName = item.Value<string>("titleBlockTypeName");
            if (!string.IsNullOrWhiteSpace(familyName) || !string.IsNullOrWhiteSpace(typeName))
            {
                return titleBlockTypes.FirstOrDefault(t =>
                           (string.IsNullOrWhiteSpace(familyName) || string.Equals(t.FamilyName, familyName.Trim(), StringComparison.OrdinalIgnoreCase))
                           && (string.IsNullOrWhiteSpace(typeName) || string.Equals(t.Name, typeName.Trim(), StringComparison.OrdinalIgnoreCase)))?.Id
                       ?? throw new ArgumentException($"Title block '{familyName}: {typeName}' not found.");
            }

            return titleBlockTypes.FirstOrDefault()?.Id
                   ?? throw new InvalidOperationException("No title block types are loaded. Load one or pass noTitleBlock=true.");
        }
    }
}
