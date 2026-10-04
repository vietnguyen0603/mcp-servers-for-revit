using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Foundations
{
    /// <summary>
    ///     create_slabs: floors / structural slabs / foundation slabs from polygons or
    ///     segment loops, with openings, level offsets, slope arrow and mark. One
    ///     transaction per call (chunk), one sub-transaction per slab.
    /// </summary>
    public class CreateSlabsEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Create Slabs";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var items = DocumentationUtils.RequireArray(parameters, "slabs");
            var ctx = new FoundationModelUtils.Context(doc);
            var results = DocumentationUtils.RunBatch(doc, "MCP: Create Slabs", items, item =>
            {
                if (!(item is JObject slab))
                    throw new ArgumentException("Each slab must be an object.");
                return CreateFromItem(ctx, slab, "offset", slab.Value<bool?>("foundation") ?? false);
            });
            return Ok($"Created {results.Count(r => r.Value<bool>("success"))} of {results.Count} slabs.",
                DocumentationUtils.Summarize(results));
        }

        /// <summary>Creates one slab from its JSON item; shared with create_foundations (capSlab).</summary>
        internal static JObject CreateFromItem(FoundationModelUtils.Context ctx, JObject item, string offsetField, bool foundation)
        {
            var doc = ctx.Doc;
            var warnings = new List<string>();
            var level = FoundationModelUtils.ResolveLevel(ctx, item["level"], warnings, out var extraOffset);
            var offset = FoundationModelUtils.ToFeet(item.Value<double?>(offsetField) ?? 0) + extraOffset;
            var structural = item.Value<bool?>("structural") ?? true;
            var type = FoundationModelUtils.ResolveSlabType(ctx, item, foundation, warnings);

            var z = level.Elevation;
            var outer = FoundationModelUtils.ReadOuterLoop(item, z);
            var openings = FoundationModelUtils.ReadOpenings(item, z);

            Line slopeArrow = null;
            double slope = 0;
            if (item["slopeArrow"] is JObject arrow && arrow.HasValues)
            {
                var start = FoundationModelUtils.ReadXY(arrow["start"], z, "slopeArrow.start");
                var end = FoundationModelUtils.ReadXY(arrow["end"], z, "slopeArrow.end");
                var run = start.DistanceTo(end);
                if (run < 10 / 304.8)
                    throw new ArgumentException("slopeArrow is shorter than 10 mm.");
                var rise = FoundationModelUtils.ToFeet(arrow.Value<double?>("riseMm")
                                                       ?? throw new ArgumentException("slopeArrow.riseMm is required."));
                slopeArrow = Line.CreateBound(start, end);
                slope = rise / run;
            }

            // Zones: validated against the boundary, the openings and each other; valid ones become
            // inner loops of this slab and separate slabs of their own type / offset.
            var outerPolygon = FoundationModelUtils.Polygon2D(outer);
            var occupied = openings
                .Select((loop, i) => new KeyValuePair<string, List<UV>>($"openings[{i}]", FoundationModelUtils.Polygon2D(loop)))
                .ToList();
            var zones = ReadZones(ctx, item, z, offset, extraOffset, foundation, outerPolygon, occupied, warnings);
            if (zones.Count > 0 && slopeArrow != null)
                warnings.Add("The slab has a slope arrow; zone and drop panel slabs are created flat.");

            var cutLoops = new List<CurveLoop>(openings);
            cutLoops.AddRange(zones.Select(zone => zone.Loop));

            var floor = FoundationModelUtils.CreateSlab(ctx, type, level, outer, cutLoops, structural, slopeArrow, slope, warnings);
            FoundationModelUtils.SetSlabOffset(floor, offset, warnings);
            FoundationModelUtils.SetMark(ctx, floor, item, warnings);

            var zoneResults = new JArray();
            foreach (var zone in zones)
            {
                var zoneFloor = FoundationModelUtils.CreateSlab(ctx, zone.Type, level, zone.Loop, new List<CurveLoop>(), structural,
                    null, 0, warnings);
                FoundationModelUtils.SetSlabOffset(zoneFloor, zone.Offset, warnings, $"zones[{zone.Index}]");
                FoundationModelUtils.SetMark(ctx, zoneFloor, zone.Item, warnings);
                zone.Floor = zoneFloor;
                zoneResults.Add(new JObject
                {
                    ["index"] = zone.Index,
                    ["id"] = zoneFloor.Id.GetValue(),
                    ["typeId"] = zone.Type.Id.GetValue(),
                    ["typeName"] = zone.Type.Name,
                    ["thicknessMm"] = FoundationModelUtils.TypeThicknessMm(zone.Type),
                    ["offsetMm"] = FoundationModelUtils.ToMm(zone.Offset),
                    ["topElevationMm"] = FoundationModelUtils.ToMm(level.Elevation + zone.Offset)
                });
            }

            var dropResults = CreateDropPanels(ctx, item, level, z, offset, type, foundation, structural, outerPolygon,
                occupied, zones, slopeArrow != null, warnings);

            doc.Regenerate();
            ctx.NeedsRegenerate = false;
            foreach (var zoneResult in zoneResults.OfType<JObject>())
            {
                var zoneFloor = zones.First(zone => zone.Index == zoneResult.Value<int>("index")).Floor;
                zoneResult["areaM2"] = FoundationModelUtils.AreaM2(zoneFloor);
            }

            var result = new JObject
            {
                ["id"] = floor.Id.GetValue(),
                ["category"] = floor.Category?.Name,
                ["typeId"] = type.Id.GetValue(),
                ["typeName"] = type.Name,
                ["thicknessMm"] = FoundationModelUtils.TypeThicknessMm(type),
                ["levelId"] = level.Id.GetValue(),
                ["levelName"] = level.Name,
                ["offsetMm"] = FoundationModelUtils.ToMm(offset),
                ["topElevationMm"] = FoundationModelUtils.ToMm(level.Elevation + offset),
                ["openings"] = openings.Count,
                ["areaM2"] = FoundationModelUtils.AreaM2(floor)
            };
            if (zoneResults.Count > 0) result["zones"] = zoneResults;
            if (dropResults.Count > 0) result["dropPanels"] = dropResults;
            FoundationModelUtils.AddWarnings(result, warnings);
            return result;
        }

        private class SlabZone
        {
            public int Index;
            public JObject Item;
            public CurveLoop Loop;
            public List<UV> Polygon;
            public FloorType Type;
            public double Offset;
            public Floor Floor;
        }

        private static List<SlabZone> ReadZones(FoundationModelUtils.Context ctx, JObject item, double z, double slabOffset,
            double extraOffset, bool foundation, List<UV> outerPolygon, List<KeyValuePair<string, List<UV>>> occupied,
            List<string> warnings)
        {
            var zones = new List<SlabZone>();
            if (!(item["zones"] is JArray array)) return zones;
            for (var i = 0; i < array.Count; i++)
            {
                if (!(array[i] is JObject zoneItem))
                    throw new ArgumentException($"zones[{i}] must be an object.");
                var loop = FoundationModelUtils.LoopFromPoints(zoneItem["boundary"] as JArray, z, $"zones[{i}].boundary");
                var polygon = FoundationModelUtils.Polygon2D(loop);
                var problem = FoundationModelUtils.InnerLoopProblem(polygon, outerPolygon, occupied);
                if (problem != null)
                {
                    warnings.Add($"zones[{i}] skipped: {problem}.");
                    continue;
                }
                if (zoneItem["thickness"] == null && zoneItem["typeId"] == null && zoneItem["typeName"] == null)
                    throw new ArgumentException($"zones[{i}] needs thickness, typeId or typeName.");

                var typeWarnings = new List<string>();
                var type = FoundationModelUtils.ResolveSlabType(ctx, zoneItem, foundation, typeWarnings);
                warnings.AddRange(typeWarnings.Select(w => $"zones[{i}]: {w}"));
                var zoneOffset = zoneItem.Value<double?>("offset");
                zones.Add(new SlabZone
                {
                    Index = i,
                    Item = zoneItem,
                    Loop = loop,
                    Polygon = polygon,
                    Type = type,
                    Offset = zoneOffset.HasValue ? FoundationModelUtils.ToFeet(zoneOffset.Value) + extraOffset : slabOffset
                });
                occupied.Add(new KeyValuePair<string, List<UV>>($"zones[{i}]", polygon));
            }
            return zones;
        }

        /// <summary>
        ///     Drop panels: slabs of thickness = depth whose top is the soffit of the slab, or of the zone
        ///     containing the panel's centroid.
        /// </summary>
        private static JArray CreateDropPanels(FoundationModelUtils.Context ctx, JObject item, Level level, double z,
            double slabOffset, FloorType slabType, bool foundation, bool structural, List<UV> outerPolygon,
            List<KeyValuePair<string, List<UV>>> occupied, List<SlabZone> zones, bool sloped, List<string> warnings)
        {
            var results = new JArray();
            if (!(item["dropPanels"] is JArray array)) return results;
            if (sloped && array.Count > 0 && zones.Count == 0)
                warnings.Add("The slab has a slope arrow; drop panels are created flat under the slab's level offset.");

            var openingPolygons = occupied.Where(o => o.Key.StartsWith("openings", StringComparison.Ordinal)).ToList();
            for (var i = 0; i < array.Count; i++)
            {
                if (!(array[i] is JObject panel))
                    throw new ArgumentException($"dropPanels[{i}] must be an object.");
                var depthMm = panel.Value<double?>("depth") ?? throw new ArgumentException($"dropPanels[{i}].depth is required.");
                if (depthMm <= 0)
                    throw new ArgumentException($"dropPanels[{i}].depth must be positive (mm).");

                var loop = FoundationModelUtils.LoopFromPoints(panel["boundary"] as JArray, z, $"dropPanels[{i}].boundary");
                var polygon = FoundationModelUtils.Polygon2D(loop);
                if (!FoundationModelUtils.PolygonWithin(polygon, outerPolygon))
                    warnings.Add($"dropPanels[{i}] is not fully inside the slab boundary.");
                foreach (var opening in openingPolygons.Where(o => FoundationModelUtils.PolygonsOverlap(polygon, o.Value)))
                    warnings.Add($"dropPanels[{i}] overlaps {opening.Key}.");

                var centroid = FoundationModelUtils.Centroid(polygon);
                var host = zones.FirstOrDefault(zone => FoundationModelUtils.PointInPolygon(centroid, zone.Polygon));
                foreach (var zone in zones.Where(zone => zone != host && FoundationModelUtils.BoundaryDistance(polygon, zone.Polygon) <= 0))
                    warnings.Add($"dropPanels[{i}] straddles zones[{zone.Index}]; it hangs under the {(host == null ? "main slab" : $"zones[{host.Index}]")} soffit.");
                if (host != null && !FoundationModelUtils.PolygonWithin(polygon, host.Polygon))
                    warnings.Add($"dropPanels[{i}] is not fully inside zones[{host.Index}]; it hangs under that zone's soffit.");

                var typeItem = panel["typeId"] != null || panel["typeName"] != null
                    ? panel
                    : new JObject { ["thickness"] = depthMm };
                var typeWarnings = new List<string>();
                var type = FoundationModelUtils.ResolveSlabType(ctx, typeItem, foundation, typeWarnings);
                warnings.AddRange(typeWarnings.Select(w => $"dropPanels[{i}]: {w}"));
                var thicknessMm = FoundationModelUtils.TypeThicknessMm(type);
                if (Math.Abs(thicknessMm - depthMm) > 0.5)
                    warnings.Add($"dropPanels[{i}]: type '{type.Name}' is {thicknessMm} mm, not the requested depth {depthMm} mm.");

                var hostTop = host?.Offset ?? slabOffset;
                var hostThickness = FoundationModelUtils.TypeThicknessFeet(host?.Type ?? slabType);
                var top = hostTop - hostThickness;
                var floor = FoundationModelUtils.CreateSlab(ctx, type, level, loop, new List<CurveLoop>(), structural, null, 0, warnings);
                FoundationModelUtils.SetSlabOffset(floor, top, warnings, $"dropPanels[{i}]");
                FoundationModelUtils.SetMark(ctx, floor, panel, warnings);

                var result = new JObject
                {
                    ["index"] = i,
                    ["id"] = floor.Id.GetValue(),
                    ["typeId"] = type.Id.GetValue(),
                    ["typeName"] = type.Name,
                    ["thicknessMm"] = thicknessMm,
                    ["offsetMm"] = FoundationModelUtils.ToMm(top),
                    ["topElevationMm"] = FoundationModelUtils.ToMm(level.Elevation + top),
                    ["bottomElevationMm"] = FoundationModelUtils.ToMm(level.Elevation + top - FoundationModelUtils.TypeThicknessFeet(type))
                };
                if (host != null) result["underZone"] = host.Index;
                results.Add(result);
            }
            return results;
        }
    }
}
