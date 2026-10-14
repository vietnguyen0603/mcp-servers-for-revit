using System.IO;
using Newtonsoft.Json.Linq;
using RegisterGeometry;

namespace RevitMCPCommandSet.Services.Structure
{
    /// <summary>create_family input for kind 'column' / 'framing' (section sizes in mm).</summary>
    internal sealed class SectionFamilySpec
    {
        public string Kind;
        public string Shape;
        public Dictionary<string, double> Values = new Dictionary<string, double>(StringComparer.Ordinal);
        public List<Point2> Points;
        public Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal);
        public bool Parametric;
        public string Material;
        public string AreaParameter;
        public string SectionShape;

        public string NameOf(string key) => Names.TryGetValue(key, out var name) ? name : key;

        public bool IsSteel => Shape == SectionProfile.I || Shape == SectionProfile.Box || Shape == SectionProfile.Cruciform ||
                               Shape == SectionProfile.Pipe;

        public IReadOnlyList<string> Keys => SectionProfile.Keys(Shape, SectionProfile.WithDefaults(Shape, Values));

        public static SectionFamilySpec Read(JObject p, string kind)
        {
            var shape = SectionProfile.Normalize(p.Value<string>("shape"))
                        ?? throw new ArgumentException(
                            "Columns and framing need shape 'I', 'box', 'cruciform', 'rect', 'circle', 'pipe' or 'polygon'.");
            var spec = new SectionFamilySpec
            {
                Kind = kind,
                Shape = shape,
                Material = p.Value<string>("material"),
                AreaParameter = p["areaParameter"]?.Type == JTokenType.Boolean ? null : p.Value<string>("areaParameter") ?? "A",
                SectionShape = p.Value<string>("sectionShape")
            };
            if (p["section"] is JObject section)
                foreach (var property in section.Properties())
                    spec.Values[property.Name] = property.Value.Value<double>();
            if (p["points"] is JArray points)
                spec.Points = points.OfType<JArray>().Select(x => new Point2(x[0].Value<double>(), x[1].Value<double>())).ToList();
            if (p["parameterNames"] is JObject names)
                foreach (var property in names.Properties())
                    spec.Names[property.Name] = property.Value.Value<string>();
            spec.Parametric = shape != SectionProfile.Polygon && (p.Value<bool?>("parametric") ?? true);

            var error = SectionProfile.Validate(shape, spec.Values, spec.Points);
            if (error != null) throw new ArgumentException(error);

            if (p["types"] is JArray types)
                foreach (var type in types.OfType<JObject>())
                {
                    if (!(type["parameters"] is JObject values)) continue;
                    var merged = new Dictionary<string, double>(spec.Values, StringComparer.Ordinal);
                    foreach (var property in values.Properties())
                    {
                        var key = spec.KeyOf(property.Name);
                        if (key != null) merged[key] = property.Value.Value<double>();
                    }

                    var typeError = SectionProfile.Validate(shape, merged, spec.Points);
                    if (typeError != null) throw new ArgumentException($"Type '{type.Value<string>("name")}': {typeError}");
                }

            return spec;
        }

        /// <summary>Section key for a key or parameter name given in a type, or null.</summary>
        public string KeyOf(string name)
        {
            foreach (var key in SectionProfile.InputKeys(Shape))
                if (key == name || NameOf(key) == name)
                    return key;
            return null;
        }

        public IEnumerable<string> ParameterNames(bool parametric)
        {
            var names = new List<string>();
            if (parametric)
            {
                names.AddRange(Keys.Select(NameOf));
                if (Shape == SectionProfile.Pipe) names.Add(NameOf("Di"));
            }

            if (AreaParameter != null) names.Add(AreaParameter);
            return names;
        }
    }

    /// <summary>
    ///     Builds structural column and framing families with a built-up or standard cross-section
    ///     (I, box girder, cruciform, rectangle, circle, pipe, polygon).
    ///     <para>
    ///         Column (Metric Structural Column.rft): the section is drawn in plan on the Lower Ref. Level and
    ///         extruded up; the bottom and top faces are locked to the Lower/Upper Ref Levels so the column follows
    ///         its base/top levels.
    ///     </para>
    ///     <para>
    ///         Framing (Metric Structural Framing - Beams and Braces.rft): the template's default geometry is
    ///         deleted, the section is drawn in the end elevation on the Member Left plane and extruded to Member
    ///         Right; the end faces are locked to Member Left/Right so the beam follows its placement line. The
    ///         section top sits on the Ref. Level (whatever the template's own beam did); circles and pipes are
    ///         centred on it, polygons use their points as given. The reported sectionTop is measured from the solid.
    ///     </para>
    ///     <para>
    ///         Parametric sections get one reference plane per edge, locked to the solid's side faces and driven by
    ///         labelled dimensions + EQ constraints about the centre planes. Framing uses its own vertical centre
    ///         plane (never a template plane: a template plane is already fixed, so EQ + depth label + a level lock
    ///         overconstrain it) and only the top plane is locked to the Ref. Level. If that fails the family is
    ///         rebuilt without the Ref. Level lock, then with fixed geometry.
    ///     </para>
    /// </summary>
    internal static class SectionFamilyBuilder
    {
        private const double MmToFeet = 1 / 304.8;
        private const double Tolerance = 1e-5;

        public static JObject Build(Autodesk.Revit.ApplicationServices.Application app, string template, string path,
            string familyName, SectionFamilySpec spec, List<string> warnings)
        {
            if (spec.Parametric)
            {
                var attempt = new List<string>();
                try
                {
                    var result = BuildOnce(app, template, path, familyName, spec, true, true, attempt);
                    warnings.AddRange(attempt);
                    return result;
                }
                catch (Exception ex)
                {
                    if (spec.Kind != "framing")
                    {
                        warnings.Add("Parametric constraints failed (" + ex.Message + "); the family was rebuilt with fixed " +
                                     "geometry, so its types cannot change the section - build one family per size.");
                    }
                    else
                    {
                        // Retry without locking the section top to the Ref. Level
                        var retry = new List<string>();
                        try
                        {
                            var result = BuildOnce(app, template, path, familyName, spec, true, false, retry);
                            warnings.Add("Locking the section top to the Ref. Level failed (" + ex.Message + "); the family " +
                                         "is parametric but the section is not held on the Ref. Level - check flexTest.topOnLevel.");
                            warnings.AddRange(retry);
                            return result;
                        }
                        catch (Exception ex2)
                        {
                            warnings.Add("Parametric constraints failed (" + ex.Message + "; without the Ref. Level lock: " +
                                         ex2.Message + "); the family was rebuilt with fixed geometry, so its types cannot " +
                                         "change the section - build one family per size.");
                        }
                    }
                }
            }

            return BuildOnce(app, template, path, familyName, spec, false, false, warnings);
        }

        // ------------------------------------------------------------------ build

        private static JObject BuildOnce(Autodesk.Revit.ApplicationServices.Application app, string template, string path,
            string familyName, SectionFamilySpec spec, bool parametric, bool anchor, List<string> warnings)
        {
            var valuesFt = spec.Values.ToDictionary(x => x.Key, x => x.Value * MmToFeet);
            var pointsFt = spec.Points?.Select(x => new Point2(x.X * MmToFeet, x.Y * MmToFeet)).ToList();
            var profile = SectionProfile.Build(spec.Shape, valuesFt, pointsFt);

            var fdoc = app.NewFamilyDocument(template)
                       ?? throw new InvalidOperationException($"Could not create a family from '{template}'.");
            try
            {
                var result = new JObject { ["parametric"] = parametric };
                Frame frame;
                Extrusion extrusion;
                var errors = new List<string>();
                using (var t = new Transaction(fdoc, "Build " + familyName))
                {
                    CollectFailures(t, errors);
                    t.Start();
                    var fm = fdoc.FamilyManager;
                    if (fm.CurrentType == null) fm.NewType(familyName);

                    frame = spec.Kind == "column" ? Frame.Column(fdoc) : Frame.Framing(fdoc, profile, warnings);

                    extrusion = CreateExtrusion(fdoc, frame, profile);
                    fdoc.Regenerate();
                    result["endsLocked"] = LockEnds(fdoc, frame, extrusion, warnings);

                    if (parametric)
                    {
                        result["facesLocked"] = Constrain(fdoc, frame, profile, extrusion, spec, anchor, warnings);
                    }

                    AddArea(fdoc, spec, profile, parametric, warnings);
                    fdoc.Regenerate();
                    if (t.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit rolled the family back: " +
                                                            (errors.Count > 0 ? string.Join("; ", errors) : "unknown error"));
                }

                if (frame.Kind == "framing")
                {
                    result["sectionTop"] = SectionTop(frame, extrusion, profile);
                    if (parametric) result["topLockedToLevel"] = frame.TopLocked;
                }

                AssociateMaterial(fdoc, extrusion, spec, warnings);
                SetSectionShape(fdoc, spec, warnings);
                result["flexTest"] = FlexTest(fdoc, frame, extrusion, spec, parametric);
                fdoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
                return result;
            }
            finally
            {
                fdoc.Close(false);
            }
        }

        /// <summary>Where the section is drawn and how the member ends are referenced in the template.</summary>
        private sealed class Frame
        {
            public string Kind;
            public XYZ Origin, U, V, Normal;
            public double Length;
            public View ProfileView;
            public List<View> EndViews = new List<View>();
            public Reference StartRef, EndRef;
            public ReferencePlane CentreU, CentreV;
            public Level Level, Upper;
            public ReferencePlane MemberRight;
            public bool TopLocked;

            public XYZ At(double u, double v) => Origin + U * u + V * v;

            public static Frame Column(Document fdoc)
            {
                var levels = new FilteredElementCollector(fdoc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                var lower = levels.FirstOrDefault(l => l.Name.IndexOf("Lower", StringComparison.OrdinalIgnoreCase) >= 0) ?? levels.FirstOrDefault();
                var upper = levels.FirstOrDefault(l => l.Name.IndexOf("Upper", StringComparison.OrdinalIgnoreCase) >= 0) ?? levels.LastOrDefault();
                if (lower == null || upper == null || upper.Elevation - lower.Elevation < 1e-3)
                    throw new InvalidOperationException("The column template needs a Lower and an Upper Ref. Level.");

                var planes = Planes(fdoc);
                var frame = new Frame
                {
                    Kind = "column",
                    Origin = new XYZ(0, 0, lower.Elevation),
                    U = XYZ.BasisX,
                    V = XYZ.BasisY,
                    Normal = XYZ.BasisZ,
                    Length = upper.Elevation - lower.Elevation,
                    Level = lower,
                    Upper = upper,
                    StartRef = lower.GetPlaneReference(),
                    EndRef = upper.GetPlaneReference(),
                    CentreU = planes.FirstOrDefault(r => Math.Abs(r.Normal.X) > 0.99 && Math.Abs(r.GetPlane().Origin.X) < 1e-6),
                    CentreV = planes.FirstOrDefault(r => Math.Abs(r.Normal.Y) > 0.99 && Math.Abs(r.GetPlane().Origin.Y) < 1e-6)
                };
                frame.ProfileView = new FilteredElementCollector(fdoc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                                        .Where(v => !v.IsTemplate && v.GenLevel != null)
                                        .OrderBy(v => v.GenLevel.Id == lower.Id ? 0 : 1).FirstOrDefault()
                                    ?? throw new InvalidOperationException("The column template has no plan view.");
                frame.EndViews.AddRange(Elevations(fdoc, XYZ.BasisY));
                frame.EndViews.AddRange(Elevations(fdoc, XYZ.BasisX));
                DeleteDefaultGeometry(fdoc);
                return frame;
            }

            public static Frame Framing(Document fdoc, SectionProfile profile, List<string> warnings)
            {
                var planes = Planes(fdoc);
                var left = planes.FirstOrDefault(r => string.Equals(r.Name, "Member Left", StringComparison.OrdinalIgnoreCase));
                var right = planes.FirstOrDefault(r => string.Equals(r.Name, "Member Right", StringComparison.OrdinalIgnoreCase));
                if (left == null || right == null || Math.Abs(left.Normal.X) < 0.99 || Math.Abs(right.Normal.X) < 0.99)
                    throw new InvalidOperationException(
                        "The framing template has no 'Member Left'/'Member Right' reference planes across X; use Metric Structural Framing - Beams and Braces.rft.");
                var xL = left.GetPlane().Origin.X;
                var xR = right.GetPlane().Origin.X;
                if (xR < xL)
                {
                    (left, right) = (right, left);
                    (xL, xR) = (xR, xL);
                }

                var level = new FilteredElementCollector(fdoc).OfClass(typeof(Level)).Cast<Level>()
                                .OrderBy(l => Math.Abs(l.Elevation)).FirstOrDefault()
                            ?? throw new InvalidOperationException("The framing template has no Ref. Level.");

                // Section top on the Ref. Level whatever the template's own (deleted) beam did. Circles are centred
                // (a labelled diameter grows about the arc centre); polygons keep their own coordinates.
                DeleteDefaultGeometry(fdoc);
                var centred = profile.Shape == SectionProfile.Polygon || profile.CircleDiameters.Count > 0;
                var zc = centred ? level.Elevation : level.Elevation - profile.MaxV;
                var frame = new Frame
                {
                    Kind = "framing",
                    Origin = new XYZ(xL, 0, zc),
                    U = XYZ.BasisY,
                    V = XYZ.BasisZ,
                    Normal = XYZ.BasisX,
                    Length = xR - xL,
                    Level = level,
                    MemberRight = right,
                    StartRef = left.GetReference(),
                    EndRef = right.GetReference(),
                    CentreU = planes.FirstOrDefault(r => Math.Abs(r.Normal.Y) > 0.99 && Math.Abs(r.GetPlane().Origin.Y) < 1e-6),
                    // Always a new, free plane (Constrain creates it): the top lock + depth label hold the V planes and
                    // the centre follows by EQ. A template plane here is already fixed, which overconstrains them.
                    CentreV = null
                };
                if (frame.Length < 1e-3) throw new InvalidOperationException("Member Left and Member Right coincide.");
                frame.ProfileView = Elevations(fdoc, XYZ.BasisX).FirstOrDefault()
                                    ?? throw new InvalidOperationException("The framing template has no end (Left/Right) elevation.");
                frame.EndViews.AddRange(new FilteredElementCollector(fdoc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .Where(v => !v.IsTemplate && v.GenLevel != null));
                frame.EndViews.AddRange(Elevations(fdoc, XYZ.BasisY));
                return frame;
            }

            private static List<ReferencePlane> Planes(Document fdoc) =>
                new FilteredElementCollector(fdoc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();

            /// <summary>Elevation views looking along the axis (either direction).</summary>
            private static IEnumerable<View> Elevations(Document fdoc, XYZ axis) =>
                new FilteredElementCollector(fdoc).OfClass(typeof(ViewSection)).Cast<View>()
                    .Where(v => !v.IsTemplate && Math.Abs(v.ViewDirection.DotProduct(axis)) > 0.99)
                    .OrderBy(v => v.ViewType == ViewType.Elevation ? 0 : 1);

            /// <summary>Deletes the template's own solids and returns their combined bounding box (null if none).</summary>
            private static BoundingBoxXYZ DeleteDefaultGeometry(Document fdoc)
            {
                var forms = new FilteredElementCollector(fdoc).WhereElementIsNotElementType().ToElements().OfType<GenericForm>().ToList();
                if (forms.Count == 0) return null;
                BoundingBoxXYZ union = null;
                foreach (var form in forms)
                {
                    var box = form.get_BoundingBox(null);
                    if (box == null) continue;
                    if (union == null) union = new BoundingBoxXYZ { Min = box.Min, Max = box.Max };
                    else
                    {
                        union.Min = new XYZ(Math.Min(union.Min.X, box.Min.X), Math.Min(union.Min.Y, box.Min.Y), Math.Min(union.Min.Z, box.Min.Z));
                        union.Max = new XYZ(Math.Max(union.Max.X, box.Max.X), Math.Max(union.Max.Y, box.Max.Y), Math.Max(union.Max.Z, box.Max.Z));
                    }
                }

                fdoc.Delete(forms.Select(f => f.Id).ToList());
                return union;
            }
        }

        private static Extrusion CreateExtrusion(Document fdoc, Frame frame, SectionProfile profile)
        {
            var loops = new CurveArrArray();
            foreach (var loop in profile.Loops)
            {
                var curves = new CurveArray();
                for (var i = 0; i < loop.Count; i++)
                {
                    var a = loop[i];
                    var b = loop[(i + 1) % loop.Count];
                    curves.Append(Line.CreateBound(frame.At(a.X, a.Y), frame.At(b.X, b.Y)));
                }

                loops.Append(curves);
            }

            foreach (var diameter in profile.CircleDiameters)
            {
                var curves = new CurveArray();
                var centre = frame.At(0, 0);
                curves.Append(Arc.Create(centre, diameter / 2, 0, Math.PI, frame.U, frame.V));
                curves.Append(Arc.Create(centre, diameter / 2, Math.PI, 2 * Math.PI, frame.U, frame.V));
                loops.Append(curves);
            }

            var sketchPlane = SketchPlane.Create(fdoc, Plane.CreateByNormalAndOrigin(frame.Normal, frame.Origin));
            var extrusion = fdoc.FamilyCreate.NewExtrusion(true, loops, sketchPlane, frame.Length)
                            ?? throw new InvalidOperationException("Could not create the extrusion.");
            fdoc.Regenerate();

            // The extrusion must run from the start reference along +Normal
            var box = extrusion.get_BoundingBox(null);
            if (box != null && ((box.Min + box.Max) / 2 - frame.Origin).DotProduct(frame.Normal) < 0)
            {
                extrusion.StartOffset = 0;
                extrusion.EndOffset = -frame.Length;
            }

            return extrusion;
        }

        // ------------------------------------------------------------------ constraints

        /// <summary>Locks the start/end faces to the levels (column) or Member Left/Right (framing).</summary>
        private static int LockEnds(Document fdoc, Frame frame, Extrusion extrusion, List<string> warnings)
        {
            var locked = 0;
            foreach (var face in PlanarFaces(extrusion))
            {
                if (Math.Abs(face.FaceNormal.DotProduct(frame.Normal)) < 0.99 || face.Reference == null) continue;
                var at = (face.Origin - frame.Origin).DotProduct(frame.Normal);
                Reference target = Math.Abs(at) < Tolerance ? frame.StartRef
                    : Math.Abs(at - frame.Length) < Tolerance ? frame.EndRef : null;
                if (target == null) continue;

                Exception last = null;
                foreach (var view in frame.EndViews)
                {
                    try
                    {
                        fdoc.FamilyCreate.NewAlignment(view, target, face.Reference).IsLocked = true;
                        locked++;
                        last = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                    }
                }

                if (last != null) warnings.Add("Could not lock a member end: " + last.Message);
            }

            if (locked < 2)
                warnings.Add(frame.Kind == "column"
                    ? $"Only {locked} of 2 column ends are locked to the Lower/Upper Ref Level; the column will not follow its levels."
                    : $"Only {locked} of 2 beam ends are locked to Member Left/Right; the beam will not follow its length.");
            return locked;
        }

        /// <summary>Reference planes per section edge, face locks, labelled dimensions and EQ constraints.</summary>
        private static int Constrain(Document fdoc, Frame frame, SectionProfile profile, Extrusion extrusion,
            SectionFamilySpec spec, bool anchorToLevel, List<string> warnings)
        {
            var fm = fdoc.FamilyManager;
            var view = frame.ProfileView;
            var parameters = new Dictionary<string, FamilyParameter>(StringComparer.Ordinal);
            var valuesFt = SectionProfile.WithDefaults(spec.Shape, spec.Values).ToDictionary(x => x.Key, x => x.Value * MmToFeet);
            foreach (var key in spec.Keys)
                parameters[key] = AddLength(fm, spec.NameOf(key), valuesFt[key]);

            var reach = Math.Max(profile.MaxU - profile.MinU, profile.MaxV - profile.MinV) / 2 + 500 * MmToFeet;
            var step = Math.Max(150 * MmToFeet, reach * 0.12);

            if (profile.CircleDiameters.Count > 0)
            {
                LabelDiameter(fdoc, frame, extrusion, profile.CircleDiameters[0], parameters["D"]);
                if (profile.CircleDiameters.Count > 1)
                {
                    var inner = AddLength(fm, spec.NameOf("Di"), profile.CircleDiameters[1]);
                    fm.SetFormula(inner, $"{spec.NameOf("D")} - 2 * {spec.NameOf("t")}");
                    LabelDiameter(fdoc, frame, extrusion, profile.CircleDiameters[1], inner);
                }

                return 0;
            }

            // Centre planes: the template's, or new ones (framing has no horizontal centre plane)
            var centreU = frame.CentreU ?? NewPlane(fdoc, frame, SectionAxis.U, 0, reach, "Section Centre U");
            var centreV = frame.CentreV ?? NewPlane(fdoc, frame, SectionAxis.V, 0, reach, "Section Centre V");
            if (frame.CentreU == null && frame.Kind == "column")
                warnings.Add("The template has no centre left/right plane; the section is centred on a new plane.");

            var planes = new Dictionary<string, ReferencePlane>(StringComparer.Ordinal);
            foreach (var p in profile.Planes)
                planes[p.Id] = NewPlane(fdoc, frame, p.Axis, p.Coord, reach, PlaneName(spec, p, frame));
            fdoc.Regenerate();

            // Every side face lies on one of the planes; lock them all or fail
            var locked = 0;
            foreach (var face in PlanarFaces(extrusion))
            {
                var n = face.FaceNormal;
                if (Math.Abs(n.DotProduct(frame.Normal)) > 0.99) continue;
                SectionAxis axis;
                if (Math.Abs(n.DotProduct(frame.U)) > 0.99) axis = SectionAxis.U;
                else if (Math.Abs(n.DotProduct(frame.V)) > 0.99) axis = SectionAxis.V;
                else throw new InvalidOperationException("A side face is not parallel to the section axes.");
                var coord = (face.Origin - frame.Origin).DotProduct(axis == SectionAxis.U ? frame.U : frame.V);
                var plane = profile.FindPlane(axis, coord, Tolerance)
                            ?? throw new InvalidOperationException($"No reference plane at {coord / MmToFeet:0.#} mm for a side face.");
                if (face.Reference == null) throw new InvalidOperationException("A side face has no reference.");
                fdoc.FamilyCreate.NewAlignment(view, planes[plane.Id].GetReference(), face.Reference).IsLocked = true;
                locked++;
            }

            // Labelled dimensions, stacked outside the section
            int nextU = 1, nextV = 1;
            foreach (var label in profile.Labels)
            {
                var a = profile.Plane(label.PlaneA);
                var b = profile.Plane(label.PlaneB);
                var line = a.Axis == SectionAxis.U
                    ? Segment(frame, view, a.Coord, profile.MaxV + step * nextU++, b.Coord, profile.MaxV + step * (nextU - 1))
                    : Segment(frame, view, profile.MaxU + step * nextV++, a.Coord, profile.MaxU + step * (nextV - 1), b.Coord);
                var refs = new ReferenceArray();
                refs.Append(planes[label.PlaneA].GetReference());
                refs.Append(planes[label.PlaneB].GetReference());
                var dimension = fdoc.FamilyCreate.NewDimension(view, line, refs)
                                ?? throw new InvalidOperationException($"Could not dimension '{label.Key}'.");
                dimension.FamilyLabel = parameters[label.Key];
            }

            // Symmetry about the centre planes
            foreach (var eq in profile.EqualConstraints)
            {
                var neg = profile.Plane(eq.Negative);
                var pos = profile.Plane(eq.Positive);
                var line = eq.Axis == SectionAxis.U
                    ? Segment(frame, view, neg.Coord, profile.MinV - step * nextU++, pos.Coord, profile.MinV - step * (nextU - 1))
                    : Segment(frame, view, profile.MinU - step * nextV++, neg.Coord, profile.MinU - step * (nextV - 1), pos.Coord);
                var refs = new ReferenceArray();
                refs.Append(planes[eq.Negative].GetReference());
                refs.Append((eq.Axis == SectionAxis.U ? centreU : centreV).GetReference());
                refs.Append(planes[eq.Positive].GetReference());
                var dimension = fdoc.FamilyCreate.NewDimension(view, line, refs)
                                ?? throw new InvalidOperationException("Could not create an EQ dimension.");
                dimension.AreSegmentsEqual = true;
            }

            // Framing: hold the section top on the Ref. Level, otherwise it floats vertically. Only our own top plane
            // is locked (one constraint); the other V planes follow from the labels and EQ.
            if (frame.Kind == "framing" && anchorToLevel && profile.TopPlaneId != null)
            {
                try
                {
                    fdoc.FamilyCreate.NewAlignment(view, frame.Level.GetPlaneReference(), planes[profile.TopPlaneId].GetReference())
                        .IsLocked = true;
                    frame.TopLocked = true;
                }
                catch (Exception ex)
                {
                    warnings.Add("The section top is not locked to the Ref. Level: " + ex.Message);
                }
            }

            return locked;
        }

        /// <summary>Where the framing section actually sits relative to the Ref. Level, measured from the solid.</summary>
        private static string SectionTop(Frame frame, Extrusion extrusion, SectionProfile profile)
        {
            if (profile.Shape == SectionProfile.Polygon) return "points relative to the Ref. Level";
            var box = extrusion.get_BoundingBox(null);
            if (box == null) return "unknown";
            var level = frame.Level.Elevation;
            if (Math.Abs(box.Max.Z - level) < 1 * MmToFeet) return "top";
            if (Math.Abs((box.Max.Z + box.Min.Z) / 2 - level) < 1 * MmToFeet) return "centre";
            return $"top at {Math.Round((box.Max.Z - level) / MmToFeet, 1)} mm from the Ref. Level";
        }

        private static string PlaneName(SectionFamilySpec spec, SectionPlane plane, Frame frame)
        {
            string side;
            if (plane.Axis == SectionAxis.U) side = plane.Side < 0 ? "Left" : "Right";
            else if (frame.Kind == "framing") side = plane.Side < 0 ? "Bottom" : "Top";
            else side = plane.Side < 0 ? "Front" : "Back";
            return $"{spec.NameOf(plane.Key)} {side}";
        }

        private static ReferencePlane NewPlane(Document fdoc, Frame frame, SectionAxis axis, double coord, double reach, string name)
        {
            var a = axis == SectionAxis.U ? frame.At(coord, reach) : frame.At(-reach, coord);
            var b = axis == SectionAxis.U ? frame.At(coord, -reach) : frame.At(reach, coord);
            var plane = fdoc.FamilyCreate.NewReferencePlane(a, b, frame.Normal, frame.ProfileView);
            var existing = new HashSet<string>(new FilteredElementCollector(fdoc).OfClass(typeof(ReferencePlane))
                .Cast<ReferencePlane>().Where(r => r.Id != plane.Id).Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            var unique = name;
            for (var i = 2; existing.Contains(unique); i++) unique = $"{name} {i}";
            plane.Name = unique;
            return plane;
        }

        /// <summary>Dimension line in profile coordinates, moved onto the view plane of an elevation.</summary>
        private static Line Segment(Frame frame, View view, double u0, double v0, double u1, double v1)
        {
            return Line.CreateBound(OnView(view, frame.At(u0, v0)), OnView(view, frame.At(u1, v1)));
        }

        private static XYZ OnView(View view, XYZ point)
        {
            if (view is ViewPlan) return point;
            var direction = view.ViewDirection;
            return point - direction * (point - view.Origin).DotProduct(direction);
        }

        /// <summary>Diameter dimension on the sketch arc of the given diameter (an edge of the solid would not drive).</summary>
        private static void LabelDiameter(Document fdoc, Frame frame, Extrusion extrusion, double diameter, FamilyParameter parameter)
        {
            Curve arc = null;
            foreach (CurveArray loop in extrusion.Sketch.Profile)
            foreach (Curve curve in loop)
                if (arc == null && curve is Arc a && curve.Reference != null && Math.Abs(a.Radius * 2 - diameter) < Tolerance)
                    arc = curve;
            if (arc == null) throw new InvalidOperationException($"No sketch arc of diameter {diameter / MmToFeet:0} mm to label.");

            var r = diameter / 2;
            var point = OnView(frame.ProfileView, frame.At(r * Math.Cos(Math.PI / 4), r * Math.Sin(Math.PI / 4)));
            var dimension = fdoc.FamilyCreate.NewDiameterDimension(frame.ProfileView, arc.Reference, point)
                            ?? throw new InvalidOperationException("Could not create a diameter dimension.");
            dimension.FamilyLabel = parameter;
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

        // ------------------------------------------------------------------ parameters

        private static FamilyParameter AddLength(FamilyManager fm, string name, double valueFt)
        {
            var parameter = fm.get_Parameter(name);
            if (parameter == null)
            {
#if REVIT2022_OR_GREATER
                parameter = fm.AddParameter(name, GroupTypeId.Geometry, SpecTypeId.Length, false);
#else
                parameter = fm.AddParameter(name, BuiltInParameterGroup.PG_GEOMETRY, ParameterType.Length, false);
#endif
            }

            fm.Set(parameter, valueFt);
            return parameter;
        }

        /// <summary>Section area parameter: a formula of the section parameters, or a fixed value.</summary>
        private static void AddArea(Document fdoc, SectionFamilySpec spec, SectionProfile profile, bool parametric, List<string> warnings)
        {
            if (spec.AreaParameter == null) return;
            var fm = fdoc.FamilyManager;
            var parameter = fm.get_Parameter(spec.AreaParameter);
            if (parameter != null)
            {
                warnings.Add($"'{spec.AreaParameter}' already exists in the template; the section area was not added.");
                return;
            }

#if REVIT2022_OR_GREATER
            parameter = fm.AddParameter(spec.AreaParameter, GroupTypeId.Structural, SpecTypeId.Area, false);
#else
            parameter = fm.AddParameter(spec.AreaParameter, BuiltInParameterGroup.PG_STRUCTURAL, ParameterType.Area, false);
#endif
            var formula = parametric ? SectionProfile.AreaFormula(spec.Shape, spec.NameOf) : null;
            if (formula != null)
            {
                try
                {
                    fm.SetFormula(parameter, formula);
                    return;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Area formula '{formula}' rejected ({ex.Message}); a fixed value is set.");
                }
            }

            fm.Set(parameter, profile.Area);
        }

        /// <summary>
        ///     Structural Material drives the solid. Its value must be a real material before associating (with
        ///     &lt;By Category&gt; Revit fails with "Document regeneration failed"). Own transaction: a failure here only
        ///     warns.
        /// </summary>
        private static void AssociateMaterial(Document fdoc, Extrusion extrusion, SectionFamilySpec spec, List<string> warnings)
        {
            var errors = new List<string>();
            try
            {
                using (var t = new Transaction(fdoc, "Structural Material"))
                {
                    CollectFailures(t, errors);
                    t.Start();
                    var fm = fdoc.FamilyManager;
                    var parameter = fm.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
                    var target = extrusion.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                    if (parameter == null || target == null || !fm.CanElementParameterBeAssociated(target))
                    {
                        t.RollBack();
                        warnings.Add("The template has no Structural Material parameter; the material was not associated.");
                        return;
                    }

                    var name = spec.Material ?? (spec.IsSteel ? "Steel" : "Concrete");
                    var materials = new FilteredElementCollector(fdoc).OfClass(typeof(Material)).Cast<Material>().ToList();
                    var material = materials.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                                   ?? materials.FirstOrDefault(m => m.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                                   ?? fdoc.GetElement(Material.Create(fdoc, name)) as Material;
                    if (material != null) fm.Set(parameter, material.Id);
                    fm.AssociateElementParameterToFamilyParameter(target, parameter);
                    fdoc.Regenerate();
                    if (t.Commit() != TransactionStatus.Committed)
                        warnings.Add("Structural Material not associated: " + string.Join("; ", errors));
                }
            }
            catch (Exception ex)
            {
                warnings.Add("Structural Material not associated: " + ex.Message);
            }
        }

        /// <summary>
        ///     Optional Family Category and Parameters > Section Shape (StructuralSectionShape enum name). Resolved by
        ///     reflection so the namespace difference between Revit versions does not matter. Failure only warns.
        /// </summary>
        private static void SetSectionShape(Document fdoc, SectionFamilySpec spec, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(spec.SectionShape)) return;
            var errors = new List<string>();
            try
            {
                Type[] types;
                try
                {
                    types = typeof(Element).Assembly.GetTypes();
                }
                catch (System.Reflection.ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(x => x != null).ToArray();
                }

                var enumType = types.FirstOrDefault(x => x.IsEnum && x.Name == "StructuralSectionShape")
                               ?? throw new InvalidOperationException("this Revit version has no StructuralSectionShape");
                var names = Enum.GetNames(enumType);
                var match = names.FirstOrDefault(n => string.Equals(n, spec.SectionShape, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException($"unknown value; use one of {string.Join(", ", names)}");
                var value = Convert.ToInt32(Enum.Parse(enumType, match));

                using (var t = new Transaction(fdoc, "Section Shape"))
                {
                    CollectFailures(t, errors);
                    t.Start();
                    var parameter = fdoc.OwnerFamily.get_Parameter(BuiltInParameter.STRUCTURAL_SECTION_SHAPE);
                    if (parameter == null || parameter.IsReadOnly || !parameter.Set(value))
                    {
                        t.RollBack();
                        throw new InvalidOperationException("the family's Section Shape parameter cannot be set");
                    }

                    if (t.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException(string.Join("; ", errors));
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Section Shape '{spec.SectionShape}' not set: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ flex

        /// <summary>
        ///     Scales every section parameter by 1.25, moves the top level / Member Right by 1000 mm, then checks the
        ///     solid's volume (= area x length), extents and length. Always rolled back.
        /// </summary>
        private static JObject FlexTest(Document fdoc, Frame frame, Extrusion extrusion, SectionFamilySpec spec, bool parametric)
        {
            var result = new JObject();
            var errors = new List<string>();
            double Mm(double feet) => Math.Round(feet / MmToFeet, 1);
            using (var t = new Transaction(fdoc, "Flex"))
            {
                CollectFailures(t, errors);
                t.Start();
                try
                {
                    var fm = fdoc.FamilyManager;
                    var scale = parametric ? 1.25 : 1.0;
                    var values = SectionProfile.WithDefaults(spec.Shape, spec.Values)
                        .ToDictionary(x => x.Key, x => x.Value * MmToFeet * scale);
                    if (parametric)
                        foreach (var key in spec.Keys)
                            fm.Set(fm.get_Parameter(spec.NameOf(key)), values[key]);

                    var extra = 1000 * MmToFeet;
                    var length = frame.Length;
                    try
                    {
                        if (frame.Kind == "column") frame.Upper.Elevation += extra;
                        else ElementTransformUtils.MoveElement(fdoc, frame.MemberRight.Id, frame.Normal * extra);
                        length += extra;
                        result["endMoved"] = true;
                    }
                    catch (Exception ex)
                    {
                        result["endMoved"] = false;
                        result["endMoveError"] = ex.Message;
                    }

                    fdoc.Regenerate();
                    var points = spec.Points?.Select(x => new Point2(x.X * MmToFeet, x.Y * MmToFeet)).ToList();
                    var expected = SectionProfile.Build(spec.Shape, values, points);
                    var volume = extrusion.get_Geometry(new Options()).OfType<Solid>().Sum(s => s.Volume);
                    var box = extrusion.get_BoundingBox(null);
                    double Extent(XYZ axis) => Math.Abs((box.Max - box.Min).DotProduct(axis));

                    var measuredArea = volume / length;
                    var areaOk = Math.Abs(measuredArea - expected.Area) <= expected.Area * 0.005;
                    var lengthOk = Math.Abs(Extent(frame.Normal) - length) <= 1 * MmToFeet;
                    var uOk = Math.Abs(Extent(frame.U) - (expected.MaxU - expected.MinU)) <= Math.Max(1 * MmToFeet, (expected.MaxU - expected.MinU) * 0.005);
                    var vOk = Math.Abs(Extent(frame.V) - (expected.MaxV - expected.MinV)) <= Math.Max(1 * MmToFeet, (expected.MaxV - expected.MinV) * 0.005);

                    var ok = areaOk && lengthOk && uOk && vOk;
                    if (frame.Kind == "framing" && spec.Shape != SectionProfile.Polygon && expected.CircleDiameters.Count == 0)
                    {
                        // The section top must stay on the Ref. Level when the sizes change
                        var topOnLevel = Math.Abs(box.Max.Z - frame.Level.Elevation) <= 1 * MmToFeet;
                        result["topOnLevel"] = topOnLevel;
                        ok = ok && topOnLevel;
                    }

                    result["ok"] = ok;
                    result["scale"] = scale;
                    result["expected"] = new JObject
                    {
                        ["areaMm2"] = Math.Round(expected.Area / MmToFeet / MmToFeet),
                        ["lengthMm"] = Mm(length),
                        ["widthMm"] = Mm(expected.MaxU - expected.MinU),
                        ["depthMm"] = Mm(expected.MaxV - expected.MinV)
                    };
                    result["measured"] = new JObject
                    {
                        ["areaMm2"] = Math.Round(measuredArea / MmToFeet / MmToFeet),
                        ["lengthMm"] = Mm(Extent(frame.Normal)),
                        ["widthMm"] = Mm(Extent(frame.U)),
                        ["depthMm"] = Mm(Extent(frame.V))
                    };

                    if (spec.AreaParameter != null && fm.get_Parameter(spec.AreaParameter) is FamilyParameter area &&
                        fm.CurrentType.AsDouble(area) is double areaValue)
                        result["areaParameterMm2"] = Math.Round(areaValue / MmToFeet / MmToFeet);
                }
                catch (Exception ex)
                {
                    result["ok"] = false;
                    result["error"] = ex.Message;
                }
                finally
                {
                    if (errors.Count > 0) result["revitErrors"] = new JArray(errors);
                    t.RollBack();
                }
            }

            return result;
        }

        // ------------------------------------------------------------------ failures

        /// <summary>Deletes warnings; records errors and rolls the transaction back instead of showing a dialog.</summary>
        private static void CollectFailures(Transaction t, List<string> errors)
        {
            var options = t.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new FailureCollector(errors));
            options.SetClearAfterRollback(true);
            t.SetFailureHandlingOptions(options);
        }

        private sealed class FailureCollector : IFailuresPreprocessor
        {
            private readonly List<string> _errors;

            public FailureCollector(List<string> errors)
            {
                _errors = errors;
            }

            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                var rollback = false;
                foreach (var failure in failuresAccessor.GetFailureMessages())
                {
                    if (failure.GetSeverity() == FailureSeverity.Warning)
                    {
                        failuresAccessor.DeleteWarning(failure);
                        continue;
                    }

                    _errors.Add(failure.GetDescriptionText());
                    rollback = true;
                }

                return rollback ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
            }
        }
    }
}
