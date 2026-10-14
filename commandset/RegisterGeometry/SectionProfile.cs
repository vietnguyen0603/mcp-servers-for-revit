using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RegisterGeometry
{
    /// <summary>Profile axis: U is across the section (plan X for columns, Y for framing), V is the depth direction.</summary>
    public enum SectionAxis
    {
        U,
        V
    }

    /// <summary>A reference plane of a parametric section, at <see cref="Coord" /> along <see cref="Axis" />.</summary>
    public sealed class SectionPlane
    {
        public SectionPlane(string id, SectionAxis axis, double coord, string key, int side)
        {
            Id = id;
            Axis = axis;
            Coord = coord;
            Key = key;
            Side = side;
        }

        public string Id { get; }
        public SectionAxis Axis { get; }
        public double Coord { get; }

        /// <summary>Section key (d, bf, tw, ...) whose dimension created the plane.</summary>
        public string Key { get; }

        /// <summary>-1 on the negative side of the centre, +1 on the positive side.</summary>
        public int Side { get; }
    }

    /// <summary>A labelled dimension between two planes, driven by the parameter of <see cref="Key" />.</summary>
    public sealed class SectionLabel
    {
        public SectionLabel(string key, string planeA, string planeB)
        {
            Key = key;
            PlaneA = planeA;
            PlaneB = planeB;
        }

        public string Key { get; }
        public string PlaneA { get; }
        public string PlaneB { get; }
    }

    /// <summary>An EQ constraint: the two planes are symmetric about the section centre plane of <see cref="Axis" />.</summary>
    public sealed class SectionEqual
    {
        public SectionEqual(SectionAxis axis, string negative, string positive)
        {
            Axis = axis;
            Negative = negative;
            Positive = positive;
        }

        public SectionAxis Axis { get; }
        public string Negative { get; }
        public string Positive { get; }
    }

    /// <summary>
    ///     Built-up / standard cross-section profiles for generated structural column and framing families:
    ///     outline loops, the reference planes and labelled dimensions that drive them, and the section area.
    ///     Values are in any consistent length unit (the command set passes millimetres or feet); the section is
    ///     centred on (0, 0). Free of Revit references so it can be unit tested.
    /// </summary>
    public sealed class SectionProfile
    {
        public const string I = "I";
        public const string Box = "box";
        public const string Cruciform = "cruciform";
        public const string Rect = "rect";
        public const string Circle = "circle";
        public const string Pipe = "pipe";
        public const string Polygon = "polygon";

        private static readonly string[] Shapes = { I, Box, Cruciform, Rect, Circle, Pipe, Polygon };

        private readonly List<IReadOnlyList<Point2>> _loops = new List<IReadOnlyList<Point2>>();
        private readonly List<double> _circles = new List<double>();
        private readonly List<SectionPlane> _planes = new List<SectionPlane>();
        private readonly List<SectionLabel> _labels = new List<SectionLabel>();
        private readonly List<SectionEqual> _equals = new List<SectionEqual>();

        private SectionProfile(string shape)
        {
            Shape = shape;
        }

        public string Shape { get; }

        /// <summary>Polygon loops; the first is the outer boundary, the others are holes.</summary>
        public IReadOnlyList<IReadOnlyList<Point2>> Loops => _loops;

        /// <summary>Diameters of concentric circles at the origin (outer first, then the hole of a pipe).</summary>
        public IReadOnlyList<double> CircleDiameters => _circles;

        public IReadOnlyList<SectionPlane> Planes => _planes;
        public IReadOnlyList<SectionLabel> Labels => _labels;
        public IReadOnlyList<SectionEqual> EqualConstraints => _equals;

        /// <summary>Id of the outermost +V plane (top of the section), or null for circles and polygons.</summary>
        public string? TopPlaneId { get; private set; }

        public double MinU { get; private set; }
        public double MaxU { get; private set; }
        public double MinV { get; private set; }
        public double MaxV { get; private set; }

        /// <summary>Net section area.</summary>
        public double Area { get; private set; }

        /// <summary>Canonical shape name, accepting 'rectangular'/'circular' and any letter case; null when unknown.</summary>
        public static string? Normalize(string? shape)
        {
            if (string.IsNullOrWhiteSpace(shape)) return null;
            var s = shape!.Trim();
            if (string.Equals(s, "rectangular", StringComparison.OrdinalIgnoreCase)) return Rect;
            if (string.Equals(s, "circular", StringComparison.OrdinalIgnoreCase)) return Circle;
            return Shapes.FirstOrDefault(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Section keys that become family parameters (box 'overhang' only when it is positive).</summary>
        public static IReadOnlyList<string> Keys(string shape, IReadOnlyDictionary<string, double>? values = null)
        {
            switch (shape)
            {
                case I: return new[] { "d", "bf", "tw", "tf" };
                case Cruciform: return new[] { "d", "bf", "tw", "tf", "d2", "bf2", "tw2", "tf2" };
                case Box:
                    return values != null && values.TryGetValue("overhang", out var e) && e > 0
                        ? new[] { "B", "H", "tw", "tf", "overhang" }
                        : new[] { "B", "H", "tw", "tf" };
                case Rect: return new[] { "b", "h" };
                case Circle: return new[] { "D" };
                case Pipe: return new[] { "D", "t" };
                default: return new string[0];
            }
        }

        /// <summary>Every key a shape accepts as input (Keys plus optional ones).</summary>
        public static IReadOnlyList<string> InputKeys(string shape)
        {
            return shape == Box ? new[] { "B", "H", "tw", "tf", "overhang" } : Keys(shape);
        }

        /// <summary>Fills optional values: the second I of a cruciform copies the first, a box has no overhang.</summary>
        public static Dictionary<string, double> WithDefaults(string shape, IReadOnlyDictionary<string, double> values)
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var pair in values) result[pair.Key] = pair.Value;
            if (shape == Cruciform)
                foreach (var key in new[] { "d", "bf", "tw", "tf" })
                    if (!result.ContainsKey(key + "2") && result.TryGetValue(key, out var v))
                        result[key + "2"] = v;
            if (shape == Box && !result.ContainsKey("overhang")) result["overhang"] = 0;
            return result;
        }

        /// <summary>Returns an error message when the values do not describe a valid section, else null.</summary>
        public static string? Validate(string shape, IReadOnlyDictionary<string, double> values, IReadOnlyList<Point2>? points = null)
        {
            var canonical = Normalize(shape);
            if (canonical == null)
                return $"Unknown section shape '{shape}' (use {string.Join(", ", Shapes)}).";
            shape = canonical;
            var v = WithDefaults(shape, values);
            if (shape == Polygon)
            {
                if (points == null || points.Count < 3) return "A polygon section needs at least 3 points.";
                if (Math.Abs(SignedArea(points)) < 1e-9) return "The polygon points enclose no area.";
                return null;
            }

            foreach (var key in InputKeys(shape))
            {
                if (!v.TryGetValue(key, out var value)) return $"Section '{shape}' needs '{key}'.";
                if (double.IsNaN(value) || double.IsInfinity(value)) return $"'{key}' must be a number.";
                if (key == "overhang" ? value < 0 : value <= 0) return $"'{key}' must be positive.";
            }

            string? CheckI(string s)
            {
                double d = v["d" + s], bf = v["bf" + s], tw = v["tw" + s], tf = v["tf" + s];
                if (tw >= bf) return $"tw{s} ({Fmt(tw)}) must be smaller than bf{s} ({Fmt(bf)}).";
                if (2 * tf >= d) return $"2 x tf{s} ({Fmt(2 * tf)}) must be smaller than d{s} ({Fmt(d)}).";
                return null;
            }

            switch (shape)
            {
                case I:
                    return CheckI("");
                case Cruciform:
                {
                    var error = CheckI("") ?? CheckI("2");
                    if (error != null) return error;
                    if (v["bf"] >= v["d2"] - 2 * v["tf2"])
                        return $"bf ({Fmt(v["bf"])}) must be smaller than d2 - 2 x tf2 ({Fmt(v["d2"] - 2 * v["tf2"])}): the flanges of the two I sections would touch. Give the second I (d2, bf2, tw2, tf2) explicitly.";
                    if (v["bf2"] >= v["d"] - 2 * v["tf"])
                        return $"bf2 ({Fmt(v["bf2"])}) must be smaller than d - 2 x tf ({Fmt(v["d"] - 2 * v["tf"])}): the flanges of the two I sections would touch.";
                    return null;
                }
                case Box:
                    if (2 * v["tf"] >= v["H"]) return $"2 x tf ({Fmt(2 * v["tf"])}) must be smaller than H ({Fmt(v["H"])}).";
                    if (2 * (v["overhang"] + v["tw"]) >= v["B"])
                        return $"2 x (overhang + tw) ({Fmt(2 * (v["overhang"] + v["tw"]))}) must be smaller than B ({Fmt(v["B"])}).";
                    return null;
                case Pipe:
                    if (2 * v["t"] >= v["D"]) return $"2 x t ({Fmt(2 * v["t"])}) must be smaller than D ({Fmt(v["D"])}).";
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>Builds the outline, planes, labels and area. Throws ArgumentException on invalid input.</summary>
        public static SectionProfile Build(string shape, IReadOnlyDictionary<string, double> values, IReadOnlyList<Point2>? points = null)
        {
            var error = Validate(shape, values, points);
            if (error != null) throw new ArgumentException(error);
            shape = Normalize(shape)!;
            var v = WithDefaults(shape, values);
            var p = new SectionProfile(shape);
            switch (shape)
            {
                case I:
                    p.BuildI(v);
                    break;
                case Cruciform:
                    p.BuildCruciform(v);
                    break;
                case Box:
                    p.BuildBox(v);
                    break;
                case Rect:
                {
                    double b = v["b"] / 2, h = v["h"] / 2;
                    p._loops.Add(Rectangle(b, h));
                    p.Pair(SectionAxis.U, "b", b);
                    p.Pair(SectionAxis.V, "h", h);
                    p.TopPlaneId = Id("h", SectionAxis.V, 1);
                    break;
                }
                case Circle:
                    p._circles.Add(v["D"]);
                    break;
                case Pipe:
                    p._circles.Add(v["D"]);
                    p._circles.Add(v["D"] - 2 * v["t"]);
                    break;
                case Polygon:
                    p._loops.Add(points!.ToList());
                    break;
            }

            p.Finish();
            return p;
        }

        /// <summary>
        ///     Revit formula for the section area in terms of the family parameters (name maps a key to its parameter
        ///     name; a pipe also needs 'Di', the inner diameter parameter). Null for polygons.
        /// </summary>
        public static string? AreaFormula(string shape, Func<string, string> name)
        {
            switch (shape)
            {
                case I:
                    return $"2 * {name("bf")} * {name("tf")} + ({name("d")} - 2 * {name("tf")}) * {name("tw")}";
                case Cruciform:
                    return $"2 * {name("bf")} * {name("tf")} + ({name("d")} - 2 * {name("tf")}) * {name("tw")} + " +
                           $"2 * {name("bf2")} * {name("tf2")} + ({name("d2")} - 2 * {name("tf2")} - {name("tw")}) * {name("tw2")}";
                case Box:
                    return $"2 * {name("B")} * {name("tf")} + 2 * ({name("H")} - 2 * {name("tf")}) * {name("tw")}";
                case Rect:
                    return $"{name("b")} * {name("h")}";
                case Circle:
                    return $"pi() * {name("D")} ^ 2 / 4";
                case Pipe:
                    return $"pi() * ({name("D")} ^ 2 - {name("Di")} ^ 2) / 4";
                default:
                    return null;
            }
        }

        public SectionPlane? FindPlane(SectionAxis axis, double coord, double tolerance)
        {
            return _planes.FirstOrDefault(x => x.Axis == axis && Math.Abs(x.Coord - coord) <= tolerance);
        }

        public SectionPlane Plane(string id) => _planes.First(x => x.Id == id);

        // ------------------------------------------------------------------ shapes

        private void BuildI(Dictionary<string, double> v)
        {
            double d = v["d"] / 2, bf = v["bf"] / 2, tw = v["tw"] / 2, tf = v["tf"];
            _loops.Add(new[]
            {
                P(-bf, d), P(bf, d), P(bf, d - tf), P(tw, d - tf), P(tw, -d + tf), P(bf, -d + tf),
                P(bf, -d), P(-bf, -d), P(-bf, -d + tf), P(-tw, -d + tf), P(-tw, d - tf), P(-bf, d - tf)
            });
            Pair(SectionAxis.U, "bf", bf);
            Pair(SectionAxis.U, "tw", tw);
            Pair(SectionAxis.V, "d", d);
            Inset(SectionAxis.V, "tf", "d", d - tf);
            TopPlaneId = Id("d", SectionAxis.V, 1);
        }

        /// <summary>
        ///     Two welded I sections crossing: the first (d along V, flanges bf x tf, web tw) is continuous; the
        ///     second (d2 along U, flanges bf2 x tf2, web tw2) is welded to both faces of the first web.
        /// </summary>
        private void BuildCruciform(Dictionary<string, double> v)
        {
            double d = v["d"] / 2, bf = v["bf"] / 2, tw = v["tw"] / 2, tf = v["tf"];
            double d2 = v["d2"] / 2, bf2 = v["bf2"] / 2, tw2 = v["tw2"] / 2, tf2 = v["tf2"];
            // First quadrant from the top flange to the right flange, clockwise
            var q = new[]
            {
                P(bf, d), P(bf, d - tf), P(tw, d - tf), P(tw, tw2), P(d2 - tf2, tw2), P(d2 - tf2, bf2), P(d2, bf2)
            };
            var loop = new List<Point2>();
            loop.AddRange(q);
            loop.AddRange(q.Reverse().Select(x => P(x.X, -x.Y)));
            loop.AddRange(q.Select(x => P(-x.X, -x.Y)));
            loop.AddRange(q.Reverse().Select(x => P(-x.X, x.Y)));
            _loops.Add(loop);

            Pair(SectionAxis.U, "bf", bf);
            Pair(SectionAxis.U, "tw", tw);
            Pair(SectionAxis.V, "d", d);
            Inset(SectionAxis.V, "tf", "d", d - tf);
            Pair(SectionAxis.U, "d2", d2);
            Inset(SectionAxis.U, "tf2", "d2", d2 - tf2);
            Pair(SectionAxis.V, "tw2", tw2);
            Pair(SectionAxis.V, "bf2", bf2);
            TopPlaneId = Id("d", SectionAxis.V, 1);
        }

        /// <summary>
        ///     Built-up box girder: flanges B x tf top and bottom, two webs tw. With overhang &gt; 0 the webs are set in
        ///     from the flange tips by that distance (flange plates projecting past the webs); 0 is a closed box.
        /// </summary>
        private void BuildBox(Dictionary<string, double> v)
        {
            double b = v["B"] / 2, h = v["H"] / 2, tw = v["tw"], tf = v["tf"], e = v["overhang"];
            if (e > 0)
            {
                _loops.Add(new[]
                {
                    P(-b, h), P(b, h), P(b, h - tf), P(b - e, h - tf), P(b - e, -h + tf), P(b, -h + tf),
                    P(b, -h), P(-b, -h), P(-b, -h + tf), P(-b + e, -h + tf), P(-b + e, h - tf), P(-b, h - tf)
                });
            }
            else
            {
                _loops.Add(Rectangle(b, h));
            }

            _loops.Add(Rectangle(b - e - tw, h - tf));
            Pair(SectionAxis.U, "B", b);
            if (e > 0) Inset(SectionAxis.U, "overhang", "B", b - e);
            Inset(SectionAxis.U, "tw", e > 0 ? "overhang" : "B", b - e - tw);
            Pair(SectionAxis.V, "H", h);
            Inset(SectionAxis.V, "tf", "H", h - tf);
            TopPlaneId = Id("H", SectionAxis.V, 1);
        }

        // ------------------------------------------------------------------ helpers

        private static string Id(string key, SectionAxis axis, int side) => $"{key}:{axis}:{(side < 0 ? "-" : "+")}";

        /// <summary>Planes at -half and +half, the dimension between them labelled by key, EQ about the centre.</summary>
        private void Pair(SectionAxis axis, string key, double half)
        {
            _planes.Add(new SectionPlane(Id(key, axis, -1), axis, -half, key, -1));
            _planes.Add(new SectionPlane(Id(key, axis, 1), axis, half, key, 1));
            _labels.Add(new SectionLabel(key, Id(key, axis, -1), Id(key, axis, 1)));
            _equals.Add(new SectionEqual(axis, Id(key, axis, -1), Id(key, axis, 1)));
        }

        /// <summary>Planes at -half and +half, each dimensioned (labelled by key) from the parent plane on its side.</summary>
        private void Inset(SectionAxis axis, string key, string parent, double half)
        {
            _planes.Add(new SectionPlane(Id(key, axis, -1), axis, -half, key, -1));
            _planes.Add(new SectionPlane(Id(key, axis, 1), axis, half, key, 1));
            _labels.Add(new SectionLabel(key, Id(parent, axis, -1), Id(key, axis, -1)));
            _labels.Add(new SectionLabel(key, Id(parent, axis, 1), Id(key, axis, 1)));
        }

        private void Finish()
        {
            for (var i = 0; i < _planes.Count; i++)
            for (var j = i + 1; j < _planes.Count; j++)
                if (_planes[i].Axis == _planes[j].Axis && Math.Abs(_planes[i].Coord - _planes[j].Coord) < 1e-9)
                    throw new ArgumentException($"Planes '{_planes[i].Id}' and '{_planes[j].Id}' coincide; the section values are degenerate.");

            var all = _loops.SelectMany(x => x).ToList();
            if (all.Count > 0)
            {
                MinU = all.Min(x => x.X);
                MaxU = all.Max(x => x.X);
                MinV = all.Min(x => x.Y);
                MaxV = all.Max(x => x.Y);
            }

            if (_circles.Count > 0)
            {
                var r = _circles[0] / 2;
                MinU = MinV = -r;
                MaxU = MaxV = r;
            }

            var area = 0.0;
            for (var i = 0; i < _loops.Count; i++)
                area += (i == 0 ? 1 : -1) * Math.Abs(SignedArea(_loops[i]));
            for (var i = 0; i < _circles.Count; i++)
                area += (i == 0 ? 1 : -1) * Math.PI * _circles[i] * _circles[i] / 4;
            Area = area;
        }

        public static double SignedArea(IReadOnlyList<Point2> loop)
        {
            var sum = 0.0;
            for (var i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                sum += a.X * b.Y - b.X * a.Y;
            }

            return sum / 2;
        }

        private static Point2 P(double u, double v) => new Point2(u, v);

        private static Point2[] Rectangle(double halfU, double halfV) =>
            new[] { P(-halfU, -halfV), P(halfU, -halfV), P(halfU, halfV), P(-halfU, halfV) };

        private static string Fmt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
