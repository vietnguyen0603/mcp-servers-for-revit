using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Access
{
    /// <summary>
    ///     Read-only overview of the active document: title, path, worksharing,
    ///     unsaved state, Revit version, length unit, active view, levels (mm)
    ///     and the titles of all open documents. Opens no transaction.
    /// </summary>
    public class GetDocumentInfoEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Get Document Info";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var app = doc.Application;

            string centralPath = null;
            if (doc.IsWorkshared)
            {
                try
                {
                    var central = doc.GetWorksharingCentralModelPath();
                    if (central != null)
                        centralPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
                }
                catch (Exception)
                {
                    // Cloud or detached models may not expose a central path.
                }
            }

            var activeView = uiDoc.ActiveView;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => new JObject
                {
                    ["id"] = l.Id.GetValue(),
                    ["name"] = l.Name,
                    ["elevation"] = DocumentationUtils.FeetToMm(l.Elevation)
                });

            var openDocuments = new JArray();
            foreach (Document open in app.Documents)
            {
                if (open.IsLinked)
                    continue;
                openDocuments.Add(new JObject
                {
                    ["title"] = open.Title,
                    ["isFamily"] = open.IsFamilyDocument,
                    ["isActive"] = open.Equals(doc)
                });
            }

            var info = new JObject
            {
                ["title"] = doc.Title,
                ["path"] = string.IsNullOrEmpty(doc.PathName) ? null : doc.PathName,
                ["isWorkshared"] = doc.IsWorkshared,
                ["centralPath"] = centralPath,
                ["isModified"] = doc.IsModified,
                ["isFamilyDocument"] = doc.IsFamilyDocument,
                ["projectName"] = doc.ProjectInformation?.Name,
                ["revitVersion"] = app.VersionNumber,
                ["revitBuild"] = app.VersionBuild,
                ["lengthUnit"] = LengthUnit(doc),
                ["activeView"] = activeView == null
                    ? null
                    : new JObject
                    {
                        ["id"] = activeView.Id.GetValue(),
                        ["name"] = activeView.Name,
                        ["type"] = activeView.ViewType.ToString()
                    },
                ["levels"] = new JArray(levels),
                ["openDocumentCount"] = openDocuments.Count,
                ["openDocuments"] = openDocuments
            };

            return Ok($"Document '{doc.Title}'.", info);
        }

        private static JObject LengthUnit(Document doc)
        {
            try
            {
#if REVIT2022_OR_GREATER
                var unitTypeId = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId();
                return new JObject
                {
                    ["symbol"] = ShortSymbol(unitTypeId.TypeId),
                    ["label"] = LabelUtils.GetLabelForUnit(unitTypeId),
                    ["typeId"] = unitTypeId.TypeId
                };
#else
                var unit = doc.GetUnits().GetFormatOptions(UnitType.UT_Length).DisplayUnits;
                return new JObject
                {
                    ["symbol"] = ShortSymbol(unit.ToString()),
                    ["label"] = LabelUtils.GetLabelFor(unit),
                    ["typeId"] = unit.ToString()
                };
#endif
            }
            catch (Exception ex)
            {
                return new JObject { ["error"] = ex.Message };
            }
        }

        /// <summary>Maps a unit type id ("autodesk.unit.unit:millimeters-1.0.1") or DisplayUnitType name to a short symbol.</summary>
        private static string ShortSymbol(string id)
        {
            var text = (id ?? string.Empty).ToLowerInvariant();
            if (text.Contains("millimeter")) return "mm";
            if (text.Contains("centimeter")) return "cm";
            if (text.Contains("decimeter")) return "dm";
            if (text.Contains("meter")) return "m";
            if (text.Contains("feetfractionalinches") || text.Contains("feet_fractional_inches")) return "ft-in";
            if (text.Contains("fractionalinches") || text.Contains("fractional_inches")) return "in";
            if (text.Contains("inch")) return "in";
            if (text.Contains("feet")) return "ft";
            return null;
        }
    }
}
