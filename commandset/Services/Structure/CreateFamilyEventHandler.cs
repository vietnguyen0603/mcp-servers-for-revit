using System.IO;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Structure
{
    /// <summary>
    ///     create_family: builds a parametric extruded family (.rfa) from a Revit
    ///     family template when the office library has none, e.g. a rectangular
    ///     barrette pile (Width x Length x Depth) or a round bored pile (Diameter x
    ///     Depth). The plan profile is locked to reference planes driven by labelled
    ///     dimensions and the extrusion end is driven by the depth parameter, so the
    ///     types flex like a hand-made family. The family is saved as .rfa, loaded
    ///     into the project and the requested types are created.
    /// </summary>
    public class CreateFamilyEventHandler : JsonParameterEventHandler
    {
        private const double MmToFeet = 1 / 304.8;

        public override string GetName() => "Create Family";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            if (doc.IsFamilyDocument)
                throw new InvalidOperationException("The active document is a family; open a project.");
            if (doc.IsModifiable)
                throw new InvalidOperationException("The active document has an open transaction.");

            var spec = FamilySpec.Read(parameters);
            var warnings = new List<string>();

            var family = FindFamily(doc, spec.Name);
            string path = null;
            JObject flex = null;
            var built = false;
            if (family == null || spec.Overwrite)
            {
                var template = ResolveTemplate(doc.Application, spec);
                var folder = spec.SaveFolder ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "revit-mcp", "families");
                Directory.CreateDirectory(folder);
                path = Path.Combine(folder, spec.Name + ".rfa");
                flex = BuildFamily(doc.Application, template, path, spec, warnings);
                built = true;

                using (var t = new Transaction(doc, "MCP: Load Family " + spec.Name))
                {
                    t.Start();
                    if (!doc.LoadFamily(path, new LoadFamilyEventHandler.OverwriteOptions(true), out family) || family == null)
                        family = FindFamily(doc, spec.Name);
                    t.Commit();
                }

                if (family == null)
                    throw new InvalidOperationException($"The family was saved to '{path}' but could not be loaded.");
            }
            else
            {
                warnings.Add($"Family '{spec.Name}' is already in the project; it was not rebuilt (pass overwrite:true to rebuild).");
            }

            var types = new JArray();
            using (var t = new Transaction(doc, "MCP: Family Types " + spec.Name))
            {
                t.Start();
                foreach (var item in spec.Types)
                    types.Add(CreateType(doc, family, spec, item, warnings));
                t.Commit();
            }

            var symbols = family.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<FamilySymbol>()
                .OrderBy(s => s.Name).Select(s => new JObject { ["typeId"] = s.Id.GetValue(), ["name"] = s.Name });

            var result = new JObject
            {
                ["familyId"] = family.Id.GetValue(),
                ["familyName"] = family.Name,
                ["category"] = family.FamilyCategory?.Name,
                ["built"] = built,
                ["path"] = path,
                ["shape"] = spec.Shape,
                ["parameters"] = new JArray(spec.ParameterNames()),
                ["createdTypes"] = types,
                ["types"] = new JArray(symbols)
            };
            if (flex != null) result["flexTest"] = flex;
            if (warnings.Count > 0) result["warnings"] = new JArray(warnings);
            return Ok($"Family '{family.Name}' {(built ? "built and loaded" : "found")}; {types.Count} type(s) created or updated.", result);
        }

        private static Family FindFamily(Document doc, string name)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        // ------------------------------------------------------------------ template

        private static string ResolveTemplate(Autodesk.Revit.ApplicationServices.Application app, FamilySpec spec)
        {
            if (!string.IsNullOrWhiteSpace(spec.TemplatePath))
            {
                if (!File.Exists(spec.TemplatePath))
                    throw new ArgumentException($"Template not found: {spec.TemplatePath}");
                return spec.TemplatePath;
            }

            var names = spec.Kind == "genericModel"
                ? new[] { "Metric Generic Model.rft", "Generic Model.rft" }
                : new[] { "Metric Structural Foundation.rft", "Structural Foundation.rft" };

            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(app.FamilyTemplatePath)) roots.Add(app.FamilyTemplatePath);
            var programData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Autodesk", "RVT " + app.VersionNumber, "Family Templates");
            roots.Add(Path.Combine(programData, "English"));
            roots.Add(Path.Combine(programData, "English-Imperial"));
            roots.Add(programData);

            foreach (var name in names)
            foreach (var root in roots.Where(Directory.Exists))
            {
                var direct = Path.Combine(root, name);
                if (File.Exists(direct)) return direct;
                var found = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
                if (found != null) return found;
            }

            throw new InvalidOperationException(
                $"No family template '{names[0]}' found under {string.Join("; ", roots)}. Pass templatePath.");
        }

        // ------------------------------------------------------------------ build

        private static JObject BuildFamily(Autodesk.Revit.ApplicationServices.Application app, string template,
            string path, FamilySpec spec, List<string> warnings)
        {
            var fdoc = app.NewFamilyDocument(template)
                       ?? throw new InvalidOperationException($"Could not create a family from '{template}'.");
            try
            {
                using (var t = new Transaction(fdoc, "Build " + spec.Name))
                {
                    t.Start();
                    var fm = fdoc.FamilyManager;
                    if (fm.CurrentType == null) fm.NewType(spec.Name);

                    var view = PlanView(fdoc);
                    var planes = new FilteredElementCollector(fdoc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();
                    var centerX = planes.FirstOrDefault(r => Math.Abs(r.Normal.X) > 0.99 && Math.Abs(r.GetPlane().Origin.X) < 1e-6);
                    var centerY = planes.FirstOrDefault(r => Math.Abs(r.Normal.Y) > 0.99 && Math.Abs(r.GetPlane().Origin.Y) < 1e-6);

                    var depth = AddLength(fm, spec.DepthName, spec.InstanceDepth, spec.Depth);
                    var normal = spec.Direction == "up" ? XYZ.BasisZ : -XYZ.BasisZ;
                    var sketchPlane = SketchPlane.Create(fdoc, Plane.CreateByNormalAndOrigin(normal, XYZ.Zero));

                    Extrusion extrusion;
                    if (spec.Shape == "circular")
                    {
                        var diameter = AddLength(fm, spec.DiameterName, false, spec.Diameter);
                        var r = spec.Diameter * MmToFeet / 2;
                        var loop = new CurveArray();
                        loop.Append(Arc.Create(XYZ.Zero, r, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
                        loop.Append(Arc.Create(XYZ.Zero, r, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
                        extrusion = Extrude(fdoc, loop, sketchPlane, spec.Depth);
                        fm.AssociateElementParameterToFamilyParameter(extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), depth);
                        fdoc.Regenerate();
                        LabelDiameter(fdoc, view, extrusion, diameter, r, warnings);
                    }
                    else
                    {
                        var width = AddLength(fm, spec.WidthName, false, spec.Width);
                        var length = AddLength(fm, spec.LengthName, false, spec.Length);
                        var w = spec.Width * MmToFeet / 2;
                        var l = spec.Length * MmToFeet / 2;
                        var reach = Math.Max(w, l) + 1000 * MmToFeet;
                        var left = RefPlane(fdoc, view, new XYZ(-w, reach, 0), new XYZ(-w, -reach, 0), "Left");
                        var right = RefPlane(fdoc, view, new XYZ(w, reach, 0), new XYZ(w, -reach, 0), "Right");
                        var front = RefPlane(fdoc, view, new XYZ(-reach, -l, 0), new XYZ(reach, -l, 0), "Front");
                        var back = RefPlane(fdoc, view, new XYZ(-reach, l, 0), new XYZ(reach, l, 0), "Back");

                        var corners = new[] { new XYZ(-w, -l, 0), new XYZ(w, -l, 0), new XYZ(w, l, 0), new XYZ(-w, l, 0) };
                        var loop = new CurveArray();
                        for (var i = 0; i < 4; i++) loop.Append(Line.CreateBound(corners[i], corners[(i + 1) % 4]));
                        extrusion = Extrude(fdoc, loop, sketchPlane, spec.Depth);
                        fm.AssociateElementParameterToFamilyParameter(extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), depth);
                        fdoc.Regenerate();

                        LockSides(fdoc, view, extrusion, left, right, front, back, warnings);
                        var offset = 600 * MmToFeet;
                        Label(fdoc, view, left, right, new XYZ(-w, l + offset, 0), new XYZ(w, l + offset, 0), width);
                        Label(fdoc, view, front, back, new XYZ(w + offset, -l, 0), new XYZ(w + offset, l, 0), length);
                        if (centerX != null)
                            Equalize(fdoc, view, left, centerX, right, new XYZ(-w, l + 2 * offset, 0), new XYZ(w, l + 2 * offset, 0), warnings);
                        else warnings.Add("Template has no centre left/right plane; Width is not kept symmetric.");
                        if (centerY != null)
                            Equalize(fdoc, view, front, centerY, back, new XYZ(w + 2 * offset, -l, 0), new XYZ(w + 2 * offset, l, 0), warnings);
                        else warnings.Add("Template has no centre front/back plane; Length is not kept symmetric.");
                    }

                    AssociateMaterial(fdoc, extrusion, warnings);
                    t.Commit();
                }

                var flex = FlexTest(fdoc, spec);
                fdoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
                return flex;
            }
            finally
            {
                fdoc.Close(false);
            }
        }

        private static ViewPlan PlanView(Document fdoc)
        {
            return new FilteredElementCollector(fdoc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                       .Where(v => !v.IsTemplate && v.GenLevel != null)
                       .OrderBy(v => Math.Abs(v.GenLevel.Elevation))
                       .FirstOrDefault()
                   ?? throw new InvalidOperationException("The family template has no plan view.");
        }

        private static FamilyParameter AddLength(FamilyManager fm, string name, bool instance, double valueMm)
        {
            var parameter = fm.get_Parameter(name);
            if (parameter == null)
            {
#if REVIT2022_OR_GREATER
                parameter = fm.AddParameter(name, GroupTypeId.Geometry, SpecTypeId.Length, instance);
#else
                parameter = fm.AddParameter(name, BuiltInParameterGroup.PG_GEOMETRY, ParameterType.Length, instance);
#endif
            }

            fm.Set(parameter, valueMm * MmToFeet);
            return parameter;
        }

        private static Extrusion Extrude(Document fdoc, CurveArray loop, SketchPlane plane, double depthMm)
        {
            var profile = new CurveArrArray();
            profile.Append(loop);
            return fdoc.FamilyCreate.NewExtrusion(true, profile, plane, depthMm * MmToFeet)
                   ?? throw new InvalidOperationException("Could not create the extrusion.");
        }

        private static ReferencePlane RefPlane(Document fdoc, View view, XYZ bubble, XYZ free, string name)
        {
            var plane = fdoc.FamilyCreate.NewReferencePlane(bubble, free, XYZ.BasisZ, view);
            plane.Name = name;
            return plane;
        }

        /// <summary>Locks each vertical side face of the extrusion to the reference plane it lies on.</summary>
        private static void LockSides(Document fdoc, View view, Extrusion extrusion, ReferencePlane left,
            ReferencePlane right, ReferencePlane front, ReferencePlane back, List<string> warnings)
        {
            var locked = 0;
            foreach (var face in PlanarFaces(extrusion))
            {
                var n = face.FaceNormal;
                ReferencePlane target = null;
                if (n.X < -0.99) target = left;
                else if (n.X > 0.99) target = right;
                else if (n.Y < -0.99) target = front;
                else if (n.Y > 0.99) target = back;
                if (target == null || face.Reference == null) continue;
                fdoc.FamilyCreate.NewAlignment(view, target.GetReference(), face.Reference).IsLocked = true;
                locked++;
            }

            if (locked != 4) warnings.Add($"Locked {locked} of 4 side faces to reference planes; the profile may not flex.");
        }

        private static IEnumerable<PlanarFace> PlanarFaces(Extrusion extrusion)
        {
            var options = new Options { ComputeReferences = true };
            foreach (var obj in extrusion.get_Geometry(options))
                if (obj is Solid solid)
                    foreach (Face face in solid.Faces)
                        if (face is PlanarFace planar)
                            yield return planar;
        }

        private static void Label(Document fdoc, View view, ReferencePlane a, ReferencePlane b, XYZ p0, XYZ p1,
            FamilyParameter parameter)
        {
            var refs = new ReferenceArray();
            refs.Append(a.GetReference());
            refs.Append(b.GetReference());
            var dimension = fdoc.FamilyCreate.NewDimension(view, Line.CreateBound(p0, p1), refs);
            dimension.FamilyLabel = parameter;
        }

        private static void Equalize(Document fdoc, View view, ReferencePlane a, ReferencePlane center, ReferencePlane b,
            XYZ p0, XYZ p1, List<string> warnings)
        {
            try
            {
                var refs = new ReferenceArray();
                refs.Append(a.GetReference());
                refs.Append(center.GetReference());
                refs.Append(b.GetReference());
                var dimension = fdoc.FamilyCreate.NewDimension(view, Line.CreateBound(p0, p1), refs);
                dimension.AreSegmentsEqual = true;
            }
            catch (Exception ex)
            {
                warnings.Add("EQ constraint failed: " + ex.Message);
            }
        }

        /// <summary>
        ///     Labels a diameter dimension on the circular sketch curve of the extrusion. It must reference the
        ///     sketch arc: a dimension on the solid's edge is not driving, so the type would not flex.
        /// </summary>
        private static void LabelDiameter(Document fdoc, View view, Extrusion extrusion, FamilyParameter diameter,
            double radius, List<string> warnings)
        {
            Curve arc = null;
            foreach (CurveArray loop in extrusion.Sketch.Profile)
            foreach (Curve curve in loop)
                if (arc == null && curve is Arc && curve.Reference != null)
                    arc = curve;

            if (arc == null)
            {
                warnings.Add("No circular sketch curve found; Diameter is not labelled (the type will not flex).");
                return;
            }

            var dimension = fdoc.FamilyCreate.NewDiameterDimension(view, arc.Reference,
                new XYZ(radius * Math.Cos(Math.PI / 4), radius * Math.Sin(Math.PI / 4), 0));
            dimension.FamilyLabel = diameter;
        }

        /// <summary>
        ///     Drives the solid's material by the built-in Structural Material type parameter. Its value must be a
        ///     real material first: associating while it is &lt;By Category&gt; fails with "Document regeneration failed".
        /// </summary>
        private static void AssociateMaterial(Document fdoc, Extrusion extrusion, List<string> warnings)
        {
            var fm = fdoc.FamilyManager;
            var material = fm.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
            var target = extrusion.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
            if (material == null || target == null || !fm.CanElementParameterBeAssociated(target)) return;

            using (var sub = new SubTransaction(fdoc))
            {
                sub.Start();
                try
                {
                    var concrete = new FilteredElementCollector(fdoc).OfClass(typeof(Material)).Cast<Material>()
                                       .FirstOrDefault(m => m.Name.IndexOf("Concrete", StringComparison.OrdinalIgnoreCase) >= 0)
                                   ?? fdoc.GetElement(Material.Create(fdoc, "Concrete")) as Material;
                    if (concrete != null) fm.Set(material, concrete.Id);
                    fm.AssociateElementParameterToFamilyParameter(target, material);
                    fdoc.Regenerate();
                    sub.Commit();
                }
                catch (Exception ex)
                {
                    sub.RollBack();
                    warnings.Add("Structural Material not associated: " + ex.Message);
                }
            }
        }

        /// <summary>Changes every driving parameter, measures the solid and rolls back.</summary>
        private static JObject FlexTest(Document fdoc, FamilySpec spec)
        {
            var fm = fdoc.FamilyManager;
            var result = new JObject();
            using (var t = new Transaction(fdoc, "Flex"))
            {
                t.Start();
                var depth = spec.Depth * 1.5;
                fm.Set(fm.get_Parameter(spec.DepthName), depth * MmToFeet);
                double expectX, expectY;
                if (spec.Shape == "circular")
                {
                    fm.Set(fm.get_Parameter(spec.DiameterName), spec.Diameter * 1.5 * MmToFeet);
                    expectX = expectY = spec.Diameter * 1.5;
                }
                else
                {
                    fm.Set(fm.get_Parameter(spec.WidthName), spec.Width * 1.5 * MmToFeet);
                    fm.Set(fm.get_Parameter(spec.LengthName), spec.Length * 1.25 * MmToFeet);
                    expectX = spec.Width * 1.5;
                    expectY = spec.Length * 1.25;
                }

                fdoc.Regenerate();
                var box = new FilteredElementCollector(fdoc).OfClass(typeof(Extrusion)).First().get_BoundingBox(null);
                double Mm(double feet) => Math.Round(feet / MmToFeet, 1);
                var sizeX = Mm(box.Max.X - box.Min.X);
                var sizeY = Mm(box.Max.Y - box.Min.Y);
                var sizeZ = Mm(box.Max.Z - box.Min.Z);
                var centred = Math.Abs(box.Max.X + box.Min.X) / MmToFeet < 1 && Math.Abs(box.Max.Y + box.Min.Y) / MmToFeet < 1;
                // Bounding boxes of arcs are approximate (a few mm on a 1.5 m circle)
                bool Near(double measured, double expected) => Math.Abs(measured - expected) <= Math.Max(1, expected * 0.005);
                result["ok"] = Near(sizeX, expectX) && Near(sizeY, expectY) && Near(sizeZ, depth) && centred;
                result["expected"] = new JArray(expectX, expectY, depth);
                result["measured"] = new JArray(sizeX, sizeY, sizeZ);
                result["centred"] = centred;
                t.RollBack();
            }

            return result;
        }

        // ------------------------------------------------------------------ types

        private static JObject CreateType(Document doc, Family family, FamilySpec spec, JObject item, List<string> warnings)
        {
            var name = item.Value<string>("name");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Each type needs a 'name'.");
            var symbols = family.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<FamilySymbol>().ToList();
            var symbol = symbols.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            var created = symbol == null;
            if (symbol == null)
                symbol = (FamilySymbol)symbols.First().Duplicate(name);

            void SetLength(string parameterName, string field)
            {
                var value = item.Value<double?>(field);
                if (!value.HasValue) return;
                var parameter = symbol.LookupParameter(parameterName);
                if (parameter == null || parameter.IsReadOnly)
                {
                    warnings.Add($"{name}: '{parameterName}' is not a type parameter (set it per instance).");
                    return;
                }

                parameter.Set(value.Value * MmToFeet);
            }

            if (spec.Shape == "circular")
            {
                SetLength(spec.DiameterName, "diameter");
            }
            else
            {
                SetLength(spec.WidthName, "width");
                SetLength(spec.LengthName, "length");
            }

            SetLength(spec.DepthName, "depth");
            if (!symbol.IsActive) symbol.Activate();
            return new JObject { ["typeId"] = symbol.Id.GetValue(), ["name"] = symbol.Name, ["created"] = created };
        }

        // ------------------------------------------------------------------ input

        private sealed class FamilySpec
        {
            public string Name, Kind, Shape, Direction, TemplatePath, SaveFolder;
            public string WidthName, LengthName, DiameterName, DepthName;
            public double Width, Length, Diameter, Depth;
            public bool InstanceDepth, Overwrite;
            public List<JObject> Types = new List<JObject>();

            public static FamilySpec Read(JObject p)
            {
                var name = p.Value<string>("name");
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("'name' is required.");
                if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new ArgumentException($"'{name}' is not a valid file name.");
                var kind = p.Value<string>("kind") ?? "foundation";
                var spec = new FamilySpec
                {
                    Name = name,
                    Kind = kind,
                    Shape = p.Value<string>("shape") ?? "rectangular",
                    Direction = p.Value<string>("direction") ?? (kind == "genericModel" ? "up" : "down"),
                    TemplatePath = p.Value<string>("templatePath"),
                    SaveFolder = p.Value<string>("saveFolder"),
                    WidthName = p.Value<string>("widthParameter") ?? "Width",
                    LengthName = p.Value<string>("lengthParameter") ?? "Length",
                    DiameterName = p.Value<string>("diameterParameter") ?? "Diameter",
                    DepthName = p.Value<string>("depthParameter") ?? "Depth",
                    Width = p.Value<double?>("width") ?? 1200,
                    Length = p.Value<double?>("length") ?? 2800,
                    Diameter = p.Value<double?>("diameter") ?? 1000,
                    Depth = p.Value<double?>("depth") ?? 10000,
                    InstanceDepth = p.Value<bool?>("instanceDepth") ?? false,
                    Overwrite = p.Value<bool?>("overwrite") ?? false
                };
                if (spec.Shape != "rectangular" && spec.Shape != "circular")
                    throw new ArgumentException("shape must be 'rectangular' or 'circular'.");
                if (spec.Width <= 0 || spec.Length <= 0 || spec.Diameter <= 0 || spec.Depth <= 0)
                    throw new ArgumentException("Sizes must be positive.");
                if (p["types"] is JArray types)
                    spec.Types = types.OfType<JObject>().ToList();
                return spec;
            }

            public IEnumerable<string> ParameterNames()
            {
                return Shape == "circular"
                    ? new[] { DiameterName, DepthName }
                    : new[] { WidthName, LengthName, DepthName };
            }
        }
    }
}
