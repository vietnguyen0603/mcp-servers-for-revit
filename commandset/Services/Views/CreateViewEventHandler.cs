using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Creates plan, section, elevation, 3D and drafting views. Each request
    ///     item is created in its own sub-transaction inside one undoable
    ///     transaction. Model coordinates are millimetres.
    /// </summary>
    public class CreateViewEventHandler : JsonParameterEventHandler
    {
        private const double DefaultSectionDepthMm = 1000;

        public override string GetName() => "Create View";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "views");
            var results = DocumentationUtils.RunBatch(doc, "MCP: Create Views", items, item => CreateView(uiDoc, (JObject)item));
            var summary = DocumentationUtils.Summarize(results);
            return Ok($"Created {results.Count(r => r.Value<bool>("success"))} of {results.Count} views.", summary);
        }

        private static object CreateView(UIDocument uiDoc, JObject item)
        {
            var doc = uiDoc.Document;
            var viewType = item.Value<string>("viewType");
            View view;

            switch ((viewType ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "floorplan":
                    view = CreatePlan(doc, item, ViewFamily.FloorPlan);
                    break;
                case "ceilingplan":
                    view = CreatePlan(doc, item, ViewFamily.CeilingPlan);
                    break;
                case "structuralplan":
                    view = CreatePlan(doc, item, ViewFamily.StructuralPlan);
                    break;
                case "section":
                    view = CreateSection(doc, item);
                    break;
                case "elevation":
                    view = CreateElevation(uiDoc, item);
                    break;
                case "threed":
                case "3d":
                    view = View3D.CreateIsometric(doc, ResolveViewFamilyType(doc, item, ViewFamily.ThreeDimensional));
                    break;
                case "drafting":
                    view = ViewDrafting.Create(doc, ResolveViewFamilyType(doc, item, ViewFamily.Drafting));
                    break;
                default:
                    throw new ArgumentException(
                        $"Unsupported viewType '{viewType}'. Use FloorPlan, CeilingPlan, StructuralPlan, Section, Elevation, ThreeD or Drafting.");
            }

            ApplyCommonSettings(doc, view, item);

            return new
            {
                viewId = view.Id.GetValue(),
                name = view.Name,
                viewType = view.ViewType.ToString(),
                scale = view.Scale
            };
        }

        private static View CreatePlan(Document doc, JObject item, ViewFamily family)
        {
            var level = ResolveLevel(doc, item)
                        ?? throw new ArgumentException("Plan views require 'levelId' or 'levelName'.");
            return ViewPlan.Create(doc, ResolveViewFamilyType(doc, item, family), level.Id);
        }

        /// <summary>
        ///     Section looking to the left of the line drawn from start to end
        ///     (Revit's convention), or to the right when <c>flip</c> is true.
        /// </summary>
        private static View CreateSection(Document doc, JObject item)
        {
            var line = item["sectionLine"] as JObject
                       ?? throw new ArgumentException("Section views require 'sectionLine' with 'start' and 'end' points (mm).");
            var start = DocumentationUtils.ReadPointMm(line["start"]);
            var end = DocumentationUtils.ReadPointMm(line["end"]);
            if (start == null || end == null)
                throw new ArgumentException("'sectionLine' requires 'start' and 'end' points (mm).");

            start = new XYZ(start.X, start.Y, 0);
            end = new XYZ(end.X, end.Y, 0);
            if (item.Value<bool?>("flip") == true)
                (start, end) = (end, start);

            var length = start.DistanceTo(end);
            if (length < 1e-3)
                throw new ArgumentException("'sectionLine' start and end must be different points.");

            var (defaultBottom, defaultTop) = DefaultVerticalRange(doc);
            var bottom = item.Value<double?>("bottomElevation") is double b ? DocumentationUtils.MmToFeet(b) : defaultBottom;
            var top = item.Value<double?>("topElevation") is double t ? DocumentationUtils.MmToFeet(t) : defaultTop;
            if (top <= bottom)
                throw new ArgumentException("'topElevation' must be greater than 'bottomElevation'.");
            var depth = DocumentationUtils.MmToFeet(item.Value<double?>("depth") ?? DefaultSectionDepthMm);
            if (depth <= 0)
                throw new ArgumentException("'depth' must be positive.");

            var direction = (end - start).Normalize();
            var transform = Transform.Identity;
            transform.Origin = (start + end) / 2;
            transform.BasisX = direction;
            transform.BasisY = XYZ.BasisZ;
            transform.BasisZ = direction.CrossProduct(XYZ.BasisZ);

            var box = new BoundingBoxXYZ
            {
                Transform = transform,
                Min = new XYZ(-length / 2, bottom, -depth),
                Max = new XYZ(length / 2, top, 0)
            };

            return ViewSection.CreateSection(doc, ResolveViewFamilyType(doc, item, ViewFamily.Section), box);
        }

        /// <summary>
        ///     Places an elevation marker at <c>origin</c> on a plan view and
        ///     rotates it so the elevation looks along <c>lookDirection</c>.
        /// </summary>
        private static View CreateElevation(UIDocument uiDoc, JObject item)
        {
            var doc = uiDoc.Document;
            var origin = DocumentationUtils.ReadPointMm(item["origin"])
                         ?? throw new ArgumentException("Elevation views require 'origin' (mm).");
            var look = item["lookDirection"] is JObject dir
                ? new XYZ(dir.Value<double>("x"), dir.Value<double>("y"), 0)
                : XYZ.BasisY;
            if (look.GetLength() < 1e-9)
                throw new ArgumentException("'lookDirection' must be a non-zero vector.");
            look = look.Normalize();

            var plan = DocumentationUtils.GetElement<ViewPlan>(doc, DocumentationUtils.ReadId(item, "planViewId"))
                       ?? uiDoc.ActiveView as ViewPlan
                       ?? FindPlanNearElevation(doc, origin.Z)
                       ?? throw new ArgumentException("Elevation views require 'planViewId' (no plan view found).");
            if (plan.IsTemplate)
                throw new ArgumentException("'planViewId' must not be a view template.");

            var scale = item.Value<int?>("scale") ?? 100;
            var marker = ElevationMarker.CreateElevationMarker(
                doc, ResolveViewFamilyType(doc, item, ViewFamily.Elevation), origin, scale);
            var view = marker.CreateElevation(doc, plan.Id, 0);
            doc.Regenerate();

            var current = view.ViewDirection.Negate();
            var angle = Math.Atan2(current.CrossProduct(look).Z, current.DotProduct(look));
            if (Math.Abs(angle) > 1e-9)
            {
                var axis = Line.CreateBound(origin, origin + XYZ.BasisZ);
                ElementTransformUtils.RotateElement(doc, marker.Id, axis, angle);
            }

            return view;
        }

        private static void ApplyCommonSettings(Document doc, View view, JObject item)
        {
            var name = item.Value<string>("name");
            if (!string.IsNullOrWhiteSpace(name))
                view.Name = name.Trim();

            var scale = item.Value<int?>("scale");
            if (scale is int s && s > 0 && view.Scale != s)
                view.Scale = s;

            var detailLevel = item.Value<string>("detailLevel");
            if (!string.IsNullOrWhiteSpace(detailLevel))
                view.DetailLevel = DocumentationUtils.ParseEnum(detailLevel, ViewDetailLevel.Medium);

            var templateId = DocumentationUtils.ReadId(item, "viewTemplateId");
            if (templateId != null)
            {
                var template = DocumentationUtils.GetElement<View>(doc, templateId);
                if (template == null || !template.IsTemplate)
                    throw new ArgumentException($"viewTemplateId {templateId} is not a view template.");
                view.ViewTemplateId = template.Id;
            }
        }

        internal static ElementId ResolveViewFamilyType(Document doc, JObject item, ViewFamily family)
        {
            var requested = DocumentationUtils.GetElement<ViewFamilyType>(doc, DocumentationUtils.ReadId(item, "viewFamilyTypeId"));
            if (requested != null)
            {
                if (requested.ViewFamily != family)
                    throw new ArgumentException(
                        $"viewFamilyTypeId {requested.Id.GetValue()} is a {requested.ViewFamily} type, expected {family}.");
                return requested.Id;
            }

            var fallback = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .Where(t => t.ViewFamily == family)
                .OrderBy(t => t.Id.GetValue())
                .FirstOrDefault();
            return fallback?.Id ?? throw new InvalidOperationException($"No {family} view family type exists in the project.");
        }

        private static Level ResolveLevel(Document doc, JObject item)
        {
            var byId = DocumentationUtils.GetElement<Level>(doc, DocumentationUtils.ReadId(item, "levelId"));
            if (byId != null)
                return byId;

            var name = item.Value<string>("levelName");
            if (string.IsNullOrWhiteSpace(name))
                return null;

            return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                       .FirstOrDefault(l => string.Equals(l.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"Level '{name}' not found.");
        }

        /// <summary>Lowest level minus 1 m to highest level plus 3 m, in feet.</summary>
        private static (double bottom, double top) DefaultVerticalRange(Document doc)
        {
            var elevations = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Select(l => l.Elevation).ToList();
            if (elevations.Count == 0)
                return (DocumentationUtils.MmToFeet(-1000), DocumentationUtils.MmToFeet(3000));
            return (elevations.Min() - DocumentationUtils.MmToFeet(1000), elevations.Max() + DocumentationUtils.MmToFeet(3000));
        }

        private static ViewPlan FindPlanNearElevation(Document doc, double z)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(p => !p.IsTemplate && p.ViewType == ViewType.FloorPlan && p.GenLevel != null)
                .OrderBy(p => Math.Abs(p.GenLevel.Elevation - z))
                .ThenBy(p => p.Id.GetValue())
                .FirstOrDefault();
        }
    }
}
