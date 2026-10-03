using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    ///     Builds <see cref="OverrideGraphicSettings" /> from the JSON override
    ///     object shared by override_graphics and create_view_filter. A surface
    ///     or cut colour without a pattern implies solid fill. The "visible"
    ///     key is not part of the settings and is handled by callers.
    /// </summary>
    internal static class GraphicOverrideBuilder
    {
        public static OverrideGraphicSettings Build(Document doc, JObject overrides)
        {
            var settings = new OverrideGraphicSettings();
            if (overrides == null)
                return settings;

            if (overrides.Value<bool?>("halftone") is bool halftone)
                settings.SetHalftone(halftone);

            if (overrides.Value<int?>("transparency") is int transparency)
            {
                if (transparency < 0 || transparency > 100)
                    throw new ArgumentException("'transparency' must be between 0 and 100.");
                settings.SetSurfaceTransparency(transparency);
            }

            if (ReadColor(overrides, "projectionLineColor") is Color projectionColor)
                settings.SetProjectionLineColor(projectionColor);
            if (ReadLineWeight(overrides, "projectionLineWeight") is int projectionWeight)
                settings.SetProjectionLineWeight(projectionWeight);
            if (overrides.Value<string>("projectionLinePattern") is string projectionPattern)
                settings.SetProjectionLinePatternId(ResolveLinePattern(doc, projectionPattern));

            if (ReadColor(overrides, "cutLineColor") is Color cutColor)
                settings.SetCutLineColor(cutColor);
            if (ReadLineWeight(overrides, "cutLineWeight") is int cutWeight)
                settings.SetCutLineWeight(cutWeight);
            if (overrides.Value<string>("cutLinePattern") is string cutLinePattern)
                settings.SetCutLinePatternId(ResolveLinePattern(doc, cutLinePattern));

            var surfaceColor = ReadColor(overrides, "surfaceForegroundColor");
            var surfacePattern = overrides.Value<string>("surfaceForegroundPattern");
            if (surfaceColor != null || surfacePattern != null)
            {
                settings.SetSurfaceForegroundPatternId(ResolveFillPattern(doc, surfacePattern));
                settings.SetSurfaceForegroundPatternVisible(true);
                if (surfaceColor != null)
                    settings.SetSurfaceForegroundPatternColor(surfaceColor);
            }

            var cutFillColor = ReadColor(overrides, "cutForegroundColor");
            var cutFillPattern = overrides.Value<string>("cutForegroundPattern");
            if (cutFillColor != null || cutFillPattern != null)
            {
                settings.SetCutForegroundPatternId(ResolveFillPattern(doc, cutFillPattern));
                settings.SetCutForegroundPatternVisible(true);
                if (cutFillColor != null)
                    settings.SetCutForegroundPatternColor(cutFillColor);
            }

            return settings;
        }

        private static Color ReadColor(JObject overrides, string name)
        {
            if (!(overrides[name] is JArray rgb))
                return null;
            if (rgb.Count != 3)
                throw new ArgumentException($"'{name}' must be [r, g, b].");
            var values = rgb.Select(v => v.Value<int>()).ToArray();
            if (values.Any(v => v < 0 || v > 255))
                throw new ArgumentException($"'{name}' components must be between 0 and 255.");
            return new Color((byte)values[0], (byte)values[1], (byte)values[2]);
        }

        private static int? ReadLineWeight(JObject overrides, string name)
        {
            var weight = overrides.Value<int?>(name);
            if (weight != null && (weight < 1 || weight > 16))
                throw new ArgumentException($"'{name}' must be between 1 and 16.");
            return weight;
        }

        private static ElementId ResolveLinePattern(Document doc, string name)
        {
            if (string.Equals(name.Trim(), "Solid", StringComparison.OrdinalIgnoreCase))
                return LinePatternElement.GetSolidPatternId();

            var pattern = new FilteredElementCollector(doc)
                .OfClass(typeof(LinePatternElement))
                .Cast<LinePatternElement>()
                .FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            return pattern?.Id ?? throw new ArgumentException($"Line pattern '{name}' not found.");
        }

        /// <summary>
        ///     Resolves a fill pattern by name, preferring drafting patterns.
        ///     Null, empty or "solid"/"solid fill" resolve to the solid fill pattern.
        /// </summary>
        private static ElementId ResolveFillPattern(Document doc, string name)
        {
            var patterns = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .ToList();

            var trimmed = name?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                var byName = patterns
                    .Where(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.GetFillPattern().Target == FillPatternTarget.Drafting ? 0 : 1)
                    .FirstOrDefault();
                if (byName != null)
                    return byName.Id;
            }

            if (string.IsNullOrEmpty(trimmed)
                || string.Equals(trimmed, "Solid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "Solid fill", StringComparison.OrdinalIgnoreCase))
            {
                var solid = patterns.FirstOrDefault(p => p.GetFillPattern().IsSolidFill);
                if (solid != null)
                    return solid.Id;
            }

            throw new ArgumentException($"Fill pattern '{name}' not found.");
        }
    }
}
