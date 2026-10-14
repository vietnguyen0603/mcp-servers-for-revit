using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Cad
{
    /// <summary>
    ///     Links or imports a DWG/DXF file (Insert > Link CAD / Import CAD)
    ///     with the usual dialog options, then pins it, sets the base level /
    ///     offset and hides layers in the target view when asked.
    /// </summary>
    public class LinkCadEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Link CAD";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var path = parameters.Value<string>("path")?.Trim();
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("'path' is required.");
            var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (extension != ".dwg" && extension != ".dxf")
                throw new ArgumentException("Only .dwg and .dxf files are supported.");
            if (!System.IO.File.Exists(path))
                throw new ArgumentException($"File not found: {path}");

            var import = string.Equals(parameters.Value<string>("mode"), "import", StringComparison.OrdinalIgnoreCase);
            if (!import && doc.IsFamilyDocument)
                throw new ArgumentException("Families cannot link CAD; use mode 'import'.");

            var currentViewOnly = parameters.Value<bool?>("currentViewOnly") ?? false;
            var positioning = (parameters.Value<string>("positioning") ?? "origin").Trim().ToLowerInvariant();
            var level = ResolveLevel(doc, parameters["placementLevel"]);

            var view = DocumentationUtils.GetElement<View>(doc, DocumentationUtils.ReadId(parameters, "viewId"));
            if (parameters["viewId"] != null && parameters["viewId"].Type != JTokenType.Null && view == null)
                throw new ArgumentException("'viewId' is not a view.");
            if (view == null && level != null && !currentViewOnly)
                view = PlanViewOf(doc, level);
            view = view ?? uiDoc.ActiveView;
            if (view == null || view.IsTemplate)
                throw new ArgumentException("A target view is required (no active view).");
            if (currentViewOnly && view.ViewType == ViewType.ThreeD)
                throw new ArgumentException("'currentViewOnly' is not possible in a 3D view.");

            var options = new DWGImportOptions
            {
                ThisViewOnly = currentViewOnly,
                OrientToView = !currentViewOnly && (parameters.Value<bool?>("orientToView") ?? false),
                AutoCorrectAlmostVHLines = parameters.Value<bool?>("correctLinesOffAxis") ?? true,
                ColorMode = ColorMode(parameters.Value<string>("colorMode")),
                Placement = Placement(positioning)
            };
            ApplyUnits(options, parameters);

            var insertion = DocumentationUtils.ReadPointMm(parameters["insertionPoint"]);
            if (insertion != null)
            {
                if (positioning != "origin")
                    throw new ArgumentException("'insertionPoint' only applies to positioning 'origin'.");
                options.ReferencePoint = insertion;
            }

            var layerSelection = (parameters.Value<string>("layerSelection") ?? "all").Trim().ToLowerInvariant();
            var layerNames = CadUtils.ReadStrings(parameters["layerNames"]);
            switch (layerSelection)
            {
                case "visible":
                    options.VisibleLayersOnly = true;
                    break;
                case "specified":
                    if (layerNames.Count == 0)
                        throw new ArgumentException("layerSelection 'specified' needs 'layerNames'.");
                    options.SetLayerSelection(layerNames);
                    break;
                case "all":
                    break;
                default:
                    throw new ArgumentException("'layerSelection' must be all, visible or specified.");
            }

            var hideLayers = CadUtils.Matcher(CadUtils.ReadStrings(parameters["hideLayers"]));
            var warnings = new List<string>();
            ImportInstance instance;
            List<string> hidden = new List<string>();

            using (var transaction = DocumentationUtils.StartTransaction(doc, import ? "MCP: Import CAD" : "MCP: Link CAD"))
            {
                ElementId id;
                var ok = import
                    ? doc.Import(path, options, view, out id)
                    : doc.Link(path, options, view, out id);
                instance = ok && id != null && id != ElementId.InvalidElementId ? doc.GetElement(id) as ImportInstance : null;
                if (instance == null)
                {
                    transaction.RollBack();
                    return Fail($"Revit could not {(import ? "import" : "link")} '{path}'.");
                }

                doc.Regenerate();

                if (!instance.ViewSpecific)
                {
                    if (level != null)
                        SetParameter(instance, BuiltInParameter.IMPORT_BASE_LEVEL, level.Id, "Base Level", warnings);
                    var offset = parameters.Value<double?>("baseOffset");
                    if (offset != null)
                        SetParameter(instance, BuiltInParameter.IMPORT_BASE_LEVEL_OFFSET,
                            DocumentationUtils.MmToFeet(offset.Value), "Base Offset", warnings);
                }
                else if (level != null)
                {
                    warnings.Add("'placementLevel' ignored: a current-view-only import belongs to its view.");
                }

                if (parameters.Value<bool?>("pin") ?? true)
                    instance.Pinned = true;

                if (hideLayers != null)
                {
                    foreach (var layer in CadUtils.Layers(instance).Where(hideLayers))
                    {
                        var category = CadUtils.LayerCategory(instance, layer);
                        try
                        {
                            if (category != null && view.CanCategoryBeHidden(category.Id))
                            {
                                view.SetCategoryHidden(category.Id, true);
                                hidden.Add(layer);
                            }
                        }
                        catch (Exception ex)
                        {
                            warnings.Add($"Could not hide layer '{layer}': {ex.Message}");
                        }
                    }

                    if (view.ViewTemplateId != ElementId.InvalidElementId && hidden.Count > 0)
                        warnings.Add("The view has a template; its V/G settings may override the hidden layers.");
                }

                var status = transaction.Commit();
                if (status != TransactionStatus.Committed)
                    return Fail($"{(import ? "Import" : "Link")} was not committed ({status}).");
            }

            var typeId = instance.GetTypeId();
            var baseLevelId = instance.get_Parameter(BuiltInParameter.IMPORT_BASE_LEVEL)?.AsElementId();
            var layers = CadUtils.Layers(instance);
            return Ok($"{(import ? "Imported" : "Linked")} '{System.IO.Path.GetFileName(path)}' as instance {instance.Id.GetValue()} " +
                      $"({layers.Count} layers) in '{view.Name}'.", new
            {
                importInstanceId = instance.Id.GetValue(),
                cadLinkTypeId = typeId.GetValue(),
                name = CadUtils.ImportName(instance),
                linked = instance.IsLinked,
                viewSpecific = instance.ViewSpecific,
                viewId = view.Id.GetValue(),
                viewName = view.Name,
                baseLevel = baseLevelId != null && baseLevelId != ElementId.InvalidElementId
                    ? (doc.GetElement(baseLevelId) as Level)?.Name
                    : null,
                pinned = instance.Pinned,
                transform = CadUtils.TransformInfo(instance.GetTotalTransform()),
                layerCount = layers.Count,
                layers,
                hiddenLayers = hidden,
                warnings
            });
        }

        private static void SetParameter(Element element, BuiltInParameter id, object value, string label, List<string> warnings)
        {
            var parameter = element.get_Parameter(id);
            if (parameter == null || parameter.IsReadOnly)
            {
                warnings.Add($"'{label}' is not writable on this instance.");
                return;
            }

            var ok = value is ElementId elementId ? parameter.Set(elementId) : parameter.Set((double)value);
            if (!ok)
                warnings.Add($"Could not set '{label}'.");
        }

        private static Level ResolveLevel(Document doc, JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token.Type == JTokenType.Integer)
                return DocumentationUtils.GetElement<Level>(doc, token.Value<long>())
                       ?? throw new ArgumentException($"Level {token} not found.");
            var name = token.Value<string>()?.Trim();
            return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                       .FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"Level '{name}' not found.");
        }

        /// <summary>A non-template plan view on the level, preferring structural then floor plans.</summary>
        private static View PlanViewOf(Document doc, Level level)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                       .Where(v => !v.IsTemplate && v.GenLevel != null && v.GenLevel.Id == level.Id)
                       .OrderBy(v => v.ViewType == ViewType.EngineeringPlan ? 0 : v.ViewType == ViewType.FloorPlan ? 1 : 2)
                       .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                       .FirstOrDefault()
                   ?? throw new ArgumentException($"Level '{level.Name}' has no plan view to place the CAD on; pass 'viewId'.");
        }

        private static ImportColorMode ColorMode(string value)
        {
            switch ((value ?? "preserve").Trim().ToLowerInvariant())
            {
                case "blackandwhite":
                case "black_and_white":
                    return ImportColorMode.BlackAndWhite;
                case "invert":
                    return ImportColorMode.Inverted;
                case "preserve":
                    return ImportColorMode.Preserved;
                default:
                    throw new ArgumentException("'colorMode' must be preserve, invert or blackAndWhite.");
            }
        }

        private static ImportPlacement Placement(string value)
        {
            switch (value)
            {
                case "origin":
                    return ImportPlacement.Origin;
                case "center":
                    return ImportPlacement.Centered;
                case "shared":
                    return ImportPlacement.Shared;
                default:
                    throw new ArgumentException("'positioning' must be origin, center or shared.");
            }
        }

        private static void ApplyUnits(DWGImportOptions options, JObject parameters)
        {
            var units = (parameters.Value<string>("units") ?? "auto").Trim().ToLowerInvariant();
            switch (units)
            {
                case "auto":
                    options.Unit = ImportUnit.Default;
                    break;
                case "mm":
                    options.Unit = ImportUnit.Millimeter;
                    break;
                case "cm":
                    options.Unit = ImportUnit.Centimeter;
                    break;
                case "dm":
                    options.Unit = ImportUnit.Decimeter;
                    break;
                case "m":
                    options.Unit = ImportUnit.Meter;
                    break;
                case "inch":
                    options.Unit = ImportUnit.Inch;
                    break;
                case "ft":
                    options.Unit = ImportUnit.Foot;
                    break;
                case "custom":
                    var scale = parameters.Value<double?>("customScale");
                    if (scale == null || scale <= 0)
                        throw new ArgumentException("units 'custom' needs a positive 'customScale'.");
                    options.Unit = ImportUnit.Custom;
                    options.CustomScale = scale.Value;
                    break;
                default:
                    throw new ArgumentException("'units' must be auto, mm, cm, dm, m, inch, ft or custom.");
            }
        }
    }
}
