using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RegisterGeometry;
using RevitMCPCommandSet.Models.DataExtraction.Register;
using RevitMCPCommandSet.Utils;
using ElementIdAlias = RevitMCPCommandSet.Utils.ElementIdExtensions;
using GeometrySupportCategory = RegisterGeometry.SupportCategory;
using GeometryCurveKind = RegisterGeometry.CurveKind;
using PlanSupportCategory = RevitMCPCommandSet.Models.DataExtraction.Register.SupportCategory;
using PlanSupportMethod = RevitMCPCommandSet.Models.DataExtraction.Register.SupportIntersectionMethod;
using PlanCurveKind = RevitMCPCommandSet.Models.DataExtraction.Register.CurveKind;

namespace RevitMCPCommandSet.Services.DataExtraction.Register.Beam
{
    /// <summary>
    ///     Orchestrates a single page of <see cref="BeamRecord"/>s. The
    ///     builder is fed the sliced identity keys, walks the matching
    ///     <see cref="BeamCollectionHelper.IdentityEntry"/> rows, and emits
    ///     one fully-populated record per beam. It also writes per-record
    ///     warnings and the per-end support ranking evidence so the
    ///     envelope can include them without re-walking the data.
    /// </summary>
    public sealed class BeamRecordBuilder
    {
        private readonly Document _hostDocument;
        private readonly BeamRegisterExtractionOptions _options;
        private readonly ToleranceSettings _tolerance;
        private readonly BeamSupportResolver _supportResolver;
        private readonly BeamGridAssociator _gridAssociator;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _parameterMap;
        private readonly bool _includeCantilevers;
        private readonly double _minClearSpanConfidence;

        public BeamRecordBuilder(
            Document hostDocument,
            BeamRegisterExtractionOptions options,
            ToleranceSettings tolerance,
            BeamSupportResolver supportResolver,
            BeamGridAssociator gridAssociator,
            IReadOnlyDictionary<string, IReadOnlyList<string>> parameterMap,
            bool includeCantilevers,
            double minClearSpanConfidence)
        {
            _hostDocument = hostDocument;
            _options = options;
            _tolerance = tolerance;
            _supportResolver = supportResolver;
            _gridAssociator = gridAssociator;
            _parameterMap = parameterMap;
            _includeCantilevers = includeCantilevers;
            _minClearSpanConfidence = minClearSpanConfidence;
        }

        public List<BeamRecord> BuildPage(
            IReadOnlyList<IdentityKey> pageKeys,
            IReadOnlyDictionary<string, BeamCollectionHelper.IdentityEntry> entriesByKey,
            IReadOnlyDictionary<string, string> gridNamesByUid)
        {
            var records = new List<BeamRecord>(pageKeys.Count);
            foreach (var key in pageKeys)
            {
                if (!entriesByKey.TryGetValue(BuildKeyString(key), out var entry)) continue;
                var record = BuildOne(entry, gridNamesByUid);
                if (!_includeCantilevers && IsCantilever(record))
                {
                    // Cantilevers omitted per request.
                    continue;
                }
                records.Add(record);
            }
            return records;
        }

        private static bool IsCantilever(BeamRecord record)
        {
            return record.SupportStart != null && record.SupportStart.IsCantilever
                && record.SupportEnd != null && record.SupportEnd.IsCantilever;
        }

        private BeamRecord BuildOne(
            BeamCollectionHelper.IdentityEntry entry,
            IReadOnlyDictionary<string, string> gridNamesByUid)
        {
            var warnings = new List<WarningEntry>();
            var record = new BeamRecord
            {
                Provenance = BuildProvenance(entry, gridNamesByUid),
                Mark = ResolveMark(entry),
                Classification = ResolveClassification(entry),
                WidthMm = 0,
                DepthMm = 0,
                LocationCurve = new BeamLocationCurveDto(),
                StartPointMm = new PlanPoint2D(),
                EndPointMm = new PlanPoint2D(),
            };

            // Width/depth: built-in first, aliases as fallback, geometry as
            // last resort. Aliases fall through to family/symbol parameters
            // via ParameterLookup.
            ResolveSection(entry, record, warnings);
            ResolveLevel(entry, record, warnings);
            ResolveLocationCurve(entry, record, warnings);
            ResolveGridReferences(record, gridNamesByUid, warnings);

            // Support resolution only makes sense for an extracted
            // centreline. Skip it when the curve kind was rejected.
            if (record.LocationCurve.CurveKind != PlanCurveKind.Line
                && record.LocationCurve.CurveKind != PlanCurveKind.Arc)
            {
                warnings.Add(RegisterWarningFactory.UnsupportedCurveKind(
                    record.Provenance.ElementId.ToString(CultureInfo.InvariantCulture),
                    record.LocationCurve.CurveKind.ToString()));
            }
            else
            {
                ResolveSupports(record, warnings);
            }

            // Clear span is reported only when both ends have reliable
            // contact stations. Cantilever rows always report null.
            double? startStation = record.SupportStart?.ContactStationMm;
            double? endStation = record.SupportEnd?.ContactStationMm;
            if (startStation.HasValue && endStation.HasValue)
            {
                double span = Math.Abs(endStation.Value - startStation.Value);
                if (span >= 0 && span <= record.CenterlineLengthMm + _tolerance.SnapMm)
                {
                    record.ClearSpanMm = span;
                }
                else
                {
                    record.ClearSpanMm = null;
                    warnings.Add(RegisterWarningFactory.Build(
                        WarningSeverity.Warning,
                        "clear_span_inconsistent",
                        $"Computed clear span {span:F1}mm exceeds centreline length; reporting null.",
                        elementId: record.Provenance.ElementId.ToString(CultureInfo.InvariantCulture)));
                }
            }

            // Drop the record when confidence is below the configured
            // threshold; otherwise tag the supportsDerivation.
            double minConfidence = Math.Min(
                record.SupportStart?.Confidence ?? 1.0,
                record.SupportEnd?.Confidence ?? 1.0);
            if (minConfidence < _minClearSpanConfidence)
            {
                record.ClearSpanMm = null;
                warnings.Add(RegisterWarningFactory.Build(
                    WarningSeverity.Info,
                    "low_clear_span_confidence",
                    $"Minimum support confidence {minConfidence:F2} below threshold {_minClearSpanConfidence:F2}; clear span null.",
                    elementId: record.Provenance.ElementId.ToString(CultureInfo.InvariantCulture)));
            }

            record.SupportsDerivation = new DerivationInfo
            {
                Method = "geometry_intersection",
                Confidence = minConfidence,
                Note = "Station derived from beam-axis projection onto support bounds.",
            };

            if (warnings.Count > 0)
            {
                record.Provenance.Warnings = warnings;
            }
            return record;
        }

        private static string BuildKeyString(IdentityKey key)
        {
            return $"{key.DocumentKey}:{key.ElementKey}";
        }

        private ElementProvenance BuildProvenance(
            BeamCollectionHelper.IdentityEntry entry,
            IReadOnlyDictionary<string, string> gridNamesByUid)
        {
            var provenance = new ElementProvenance
            {
                ElementId = entry.ElementId,
                UniqueId = entry.UniqueId,
                DocumentKey = entry.DocumentKey,
                LinkInstanceUniqueId = entry.LinkInstanceUniqueId,
                Category = entry.Instance.Category?.Name,
            };
            try
            {
                var type = entry.Instance.Symbol;
                provenance.FamilyName = type?.Family?.Name;
                provenance.TypeName = type?.Name;
            }
            catch (InvalidOperationException)
            {
                // Type missing; leave the type fields null.
            }

            // Level info: prefer the parameter-bound reference level, but
            // fall back to the level id parameter when only that exists.
            try
            {
                var levelParam = entry.Instance.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
                var levelId = levelParam?.AsElementId();
                if (levelId != null && levelId != ElementId.InvalidElementId)
                {
                    var level = entry.Document.GetElement(levelId) as Level;
                    if (level != null)
                    {
                        provenance.LevelId = ElementIdAlias.GetValue(level.Id).ToString(CultureInfo.InvariantCulture);
                        provenance.LevelName = level.Name;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Reference level not bound; ignore.
            }
            return provenance;
        }

        private string ResolveMark(BeamCollectionHelper.IdentityEntry entry)
        {
            // Built-in mark first.
            try
            {
                var markParam = entry.Instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                if (markParam != null && !string.IsNullOrEmpty(markParam.AsString()))
                {
                    return markParam.AsString();
                }
            }
            catch (InvalidOperationException)
            {
            }
            // Aliases fallback.
            var lookup = new ParameterLookup(entry.Instance, _parameterMap);
            return lookup.ResolveString("mark");
        }

        private string ResolveClassification(BeamCollectionHelper.IdentityEntry entry)
        {
            // Custom aliases map: the server may send beam type aliases
            // keyed by HB/VB/SP/B. The ParameterLookup walks them in order,
            // trying built-in first then project/family parameters by name.
            var lookup = new ParameterLookup(entry.Instance, _parameterMap);
            // Built-in classification lookup falls back to the family/type
            // name to avoid coupling to a project-specific parameter.
            string fromFamily = null;
            try
            {
                var type = entry.Instance.Symbol;
                fromFamily = type?.Family?.Name;
            }
            catch (InvalidOperationException)
            {
            }
            // Walk the configured aliases; the first alias that resolves a
            // non-empty value wins.
            if (_options.BeamTypeAliases != null)
            {
                foreach (var kv in _options.BeamTypeAliases)
                {
                    if (kv.Value == null) continue;
                    foreach (var alias in kv.Value)
                    {
                        var resolved = lookup.ResolveString(alias);
                        if (!string.IsNullOrEmpty(resolved))
                        {
                            return resolved;
                        }
                    }
                }
            }
            // Default: derive a 2-letter classification from the family
            // name when no aliases were configured.
            return DeriveClassificationFromFamily(fromFamily);
        }

        private static string DeriveClassificationFromFamily(string familyName)
        {
            if (string.IsNullOrEmpty(familyName)) return null;
            // Best-effort defaults: HB / VB / SP / B codes.
            var upper = familyName.ToUpperInvariant();
            if (upper.Contains("HB")) return "HB";
            if (upper.Contains("VB")) return "VB";
            if (upper.Contains("SP")) return "SP";
            if (upper.Contains("BEAM")) return "B";
            return null;
        }

        private void ResolveSection(
            BeamCollectionHelper.IdentityEntry entry,
            BeamRecord record,
            List<WarningEntry> warnings)
        {
            // Width/Depth via the ParameterLookup (built-in + aliases).
            var lookup = new ParameterLookup(entry.Instance, _parameterMap);
            double? widthMm = ResolveDoubleAsMm(lookup, "width", "b", "Width", "Beam_Width");
            double? depthMm = ResolveDoubleAsMm(lookup, "depth", "h", "Depth", "Beam_Depth");
            if (widthMm.HasValue) record.WidthMm = widthMm.Value;
            if (depthMm.HasValue) record.DepthMm = depthMm.Value;

            // Geometry fallback when parameter lookup failed.
            if (!widthMm.HasValue || !depthMm.HasValue)
            {
                ResolveSectionFromGeometry(entry, record, warnings);
            }
        }

        private static double? ResolveDoubleAsMm(
            ParameterLookup lookup,
            params string[] keys)
        {
            foreach (var key in keys)
            {
                var raw = lookup.ResolveDouble(key);
                if (raw.HasValue) return raw.Value;
            }
            return null;
        }

        private void ResolveSectionFromGeometry(
            BeamCollectionHelper.IdentityEntry entry,
            BeamRecord record,
            List<WarningEntry> warnings)
        {
            // The beam's geometry solid gives the tightest bounds in plan;
            // the section dimensions live in the bounding box, with depth
            // coming from the Z extent.
            try
            {
                var bbox = entry.Instance.get_BoundingBox(null);
                if (bbox == null) return;
                double widthMm = RegisterUnitConverter.MmFromFeet(Math.Abs(bbox.Max.X - bbox.Min.X));
                double depthMm = RegisterUnitConverter.MmFromFeet(Math.Abs(bbox.Max.Y - bbox.Min.Y));
                if (record.WidthMm <= 0) record.WidthMm = widthMm;
                if (record.DepthMm <= 0) record.DepthMm = depthMm;
                record.WidthSourceParameter ??= "geometry_extent";
                record.DepthSourceParameter ??= "geometry_extent";
            }
            catch (InvalidOperationException ex)
            {
                warnings.Add(RegisterWarningFactory.SolidExtractionFailed(
                    record.Provenance.ElementId.ToString(CultureInfo.InvariantCulture),
                    ex.Message));
            }
        }

        private void ResolveLevel(
            BeamCollectionHelper.IdentityEntry entry,
            BeamRecord record,
            List<WarningEntry> warnings)
        {
            // Offset: derived from the Z elevation relative to the
            // reference level. The reference level is read via the
            // built-in parameter rather than the FamilyInstance property
            // because the latter is absent on older Revit bindings.
            double levelElevMm = 0;
            try
            {
                var refLevelParam = entry.Instance.get_Parameter(
                    BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
                var refLevelId = refLevelParam?.AsElementId();
                if (refLevelId != null && refLevelId != ElementId.InvalidElementId)
                {
                    var refLevel = entry.Document.GetElement(refLevelId) as Level;
                    if (refLevel != null)
                    {
                        levelElevMm = RegisterUnitConverter.MmFromFeet(refLevel.Elevation);
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                var bb = entry.Instance.get_BoundingBox(null);
                if (bb != null)
                {
                    double zMinMm = RegisterUnitConverter.MmFromFeet(bb.Min.Z);
                    record.LevelOffsetMm = zMinMm - levelElevMm;
                }
            }
            catch (InvalidOperationException)
            {
                record.LevelOffsetMm = 0;
            }
        }

        private void ResolveLocationCurve(
            BeamCollectionHelper.IdentityEntry entry,
            BeamRecord record,
            List<WarningEntry> warnings)
        {
            if (entry.Instance.Location is not LocationCurve locationCurve
                || locationCurve.Curve == null)
            {
                warnings.Add(RegisterWarningFactory.Build(
                    WarningSeverity.Warning,
                    "beam_missing_location",
                    "Structural framing instance has no LocationCurve; emitting zero-length stub.",
                    elementId: record.Provenance.ElementId.ToString(CultureInfo.InvariantCulture)));
                record.CenterlineLengthMm = 0;
                record.ProjectedLengthMm = 0;
                return;
            }

            var curve = locationCurve.Curve;
            var measurement = RegisterCurveAdapter.ToPlanMeasurementsMm(curve);
            record.LocationCurve = new BeamLocationCurveDto
            {
                CurveKind = curve is Line ? PlanCurveKind.Line : PlanCurveKind.Arc,
                StartMm = new PlanPoint2D { X = measurement.start.Xmm, Y = measurement.start.Ymm },
                EndMm = new PlanPoint2D { X = measurement.end.Xmm, Y = measurement.end.Ymm },
                StartZElevationMm = measurement.zStartMm,
                EndZElevationMm = measurement.zEndMm,
            };
            record.StartPointMm = new PlanPoint2D
            {
                X = measurement.start.Xmm,
                Y = measurement.start.Ymm,
            };
            record.EndPointMm = new PlanPoint2D
            {
                X = measurement.end.Xmm,
                Y = measurement.end.Ymm,
            };
            record.CenterlineLengthMm = measurement.lengthMm;
            // Projected length is the planar distance between the two end
            // points; it diverges from the centreline length for sloped
            // beams.
            double dx = measurement.end.Xmm - measurement.start.Xmm;
            double dy = measurement.end.Ymm - measurement.start.Ymm;
            record.ProjectedLengthMm = Math.Sqrt(dx * dx + dy * dy);
        }

        private void ResolveGridReferences(
            BeamRecord record,
            IReadOnlyDictionary<string, string> gridNamesByUid,
            List<WarningEntry> warnings)
        {
            var start = new Point2(record.StartPointMm.X, record.StartPointMm.Y);
            var end = new Point2(record.EndPointMm.X, record.EndPointMm.Y);
            var refs = _gridAssociator.ResolveReferences(start, end);
            record.AxisGridStart = refs.AxisGridStart;
            record.AxisGridEnd = refs.AxisGridEnd;
            record.FromGrid = refs.FromGrid;
            record.ToGrid = refs.ToGrid;
        }

        private void ResolveSupports(BeamRecord record, List<WarningEntry> warnings)
        {
            var beamStart = new Point2(record.StartPointMm.X, record.StartPointMm.Y);
            var beamEnd = new Point2(record.EndPointMm.X, record.EndPointMm.Y);
            // Convert back to Revit XYZ for the resolver's coordinate
            // adapter. The conversion is exact because we kept the mm
            // values around.
            var beamStartFeet = new XYZ(
                RegisterUnitConverter.FeetFromMm(beamStart.X),
                RegisterUnitConverter.FeetFromMm(beamStart.Y),
                0);
            var beamEndFeet = new XYZ(
                RegisterUnitConverter.FeetFromMm(beamEnd.X),
                RegisterUnitConverter.FeetFromMm(beamEnd.Y),
                0);

            var startResolution = _supportResolver.Resolve(beamStartFeet, beamEndFeet, BeamEnd.Start);
            var endResolution = _supportResolver.Resolve(beamStartFeet, beamEndFeet, BeamEnd.End);
            record.SupportStart = ToSupportRecord(startResolution, record);
            record.SupportEnd = ToSupportRecord(endResolution, record);
        }

        private BeamSupportRecord ToSupportRecord(
            SupportResolution resolution,
            BeamRecord beam)
        {
            var record = new BeamSupportRecord
            {
                SupportElementId = resolution.Candidate?.ElementId,
                SupportUniqueId = resolution.Candidate?.UniqueId,
                SupportMark = resolution.Candidate?.Mark,
                SupportCategory = MapCategory(resolution.Candidate?.Category ?? GeometrySupportCategory.Unknown),
                IntersectionMethod = PlanSupportMethod.BoundsFallback,
                Confidence = resolution.Candidate != null ? 1.0 : 0.0,
                IsCantilever = resolution.IsCantilever,
                ContactStationMm = resolution.ContactStationMm,
            };

            if (resolution.Candidate != null)
            {
                var contact = resolution.Candidate.ContactPoint;
                record.ContactPointMm = new PlanPoint2D { X = contact.X, Y = contact.Y };
            }

            if (beam.LocationCurve.CurveKind == PlanCurveKind.Line || beam.LocationCurve.CurveKind == PlanCurveKind.Arc)
            {
                // Evidence: top candidates regardless of whether one was
                // selected; limited to the first 5 to keep the envelope
                // compact.
                if (resolution.Considered != null && resolution.Considered.Count > 0)
                {
                    var top = new List<SupportCandidateDto>(Math.Min(5, resolution.Considered.Count));
                    int categoryRank = 0;
                    foreach (var c in resolution.Considered.Take(5))
                    {
                        top.Add(new SupportCandidateDto
                        {
                            SupportElementId = c.ElementId ?? 0,
                            SupportUniqueId = c.UniqueId,
                            SupportCategory = MapCategory(c.Category),
                            DistanceMm = c.DistanceMm,
                            CategoryRank = categoryRank++,
                            TotalScore = c.DistanceMm,
                            Accepted = c == resolution.Candidate,
                            RejectionReason = c == resolution.Candidate
                                ? null
                                : "outranked",
                        });
                    }
                    record.RankingEvidence = new SupportRankingEvidence
                    {
                        SearchRadiusMm = _tolerance.SupportSearchMm,
                        CandidatesConsidered = resolution.Considered.Count,
                        TopCandidates = top,
                    };
                }
            }
            return record;
        }

        private static PlanSupportCategory MapCategory(GeometrySupportCategory category)
        {
            return category switch
            {
                GeometrySupportCategory.Wall => PlanSupportCategory.Wall,
                GeometrySupportCategory.Column => PlanSupportCategory.Column,
                GeometrySupportCategory.Beam => PlanSupportCategory.Beam,
                GeometrySupportCategory.Other => PlanSupportCategory.Other,
                _ => PlanSupportCategory.Unknown,
            };
        }
    }
}