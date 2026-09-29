using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using RegisterGeometry;
using RevitMCPCommandSet.Models.DataExtraction.Register;
using GeometryAxisFamily = RegisterGeometry.AxisFamily;
using PlanAxisFamily = RevitMCPCommandSet.Models.DataExtraction.Register.AxisFamily;

namespace RevitMCPCommandSet.Services.DataExtraction.Register.Beam
{
    /// <summary>
    ///     Builds a <see cref="GridRegistry"/> from the active document and any
    ///     requested linked documents, then resolves axis/from/to grid
    ///     references at the start and end of a beam. Skew and radial grids
    ///     produce null references because their scalar coordinate cannot be
    ///     represented in the schema; the registry still owns them so the
    ///     ranker can fall back to distance-based matching.
    /// </summary>
    public sealed class BeamGridAssociator
    {
        private readonly ToleranceSettings _tolerance;
        private readonly GridRegistry _registry;

        public BeamGridAssociator(
            ToleranceSettings tolerance,
            GridRegistry registry)
        {
            _tolerance = tolerance;
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public GridRegistry Registry => _registry;

        /// <summary>
        ///     Build a registry from the supplied grids. Each grid is
        ///     classified by <see cref="DirectionClassifier"/>; explicit
        ///     axis assignments (when configured) override the clusterer.
        /// </summary>
        public static BeamGridAssociator FromDocument(
            ToleranceSettings tolerance,
            IEnumerable<Grid> grids,
            IDictionary<string, PlanAxisFamily> axisAssignments = null)
        {
            if (grids == null) throw new ArgumentNullException(nameof(grids));

            var classifier = new DirectionClassifier(tolerance);
            var pool = new List<GridLine>(64);
            foreach (var grid in grids)
            {
                if (grid == null) continue;
                var curveResult = RegisterCurveAdapter.ToCurve2(grid.Curve);
                if (!curveResult.IsConverted) continue;
                GeometryAxisFamily family;
                if (axisAssignments != null
                    && axisAssignments.TryGetValue(grid.UniqueId, out var explicitFamily))
                {
                    family = MapPlanFamily(explicitFamily);
                }
                else
                {
                    family = classifier.Classify(curveResult.Curve.Tangent);
                }
                pool.Add(new GridLine(grid.UniqueId, grid.Name, curveResult.Curve, family));
            }
            return new BeamGridAssociator(tolerance, new GridRegistry(pool));
        }

        private static GeometryAxisFamily MapPlanFamily(PlanAxisFamily plan)
        {
            return plan switch
            {
                PlanAxisFamily.X => GeometryAxisFamily.X,
                PlanAxisFamily.Y => GeometryAxisFamily.Y,
                PlanAxisFamily.Skew => GeometryAxisFamily.Skew,
                PlanAxisFamily.Radial => GeometryAxisFamily.Radial,
                _ => GeometryAxisFamily.Unknown,
            };
        }

        /// <summary>
        ///     Resolve the four grid references that annotate a beam record.
        ///     Any reference whose nearest grid is outside the intersection
        ///     tolerance returns null so callers can keep the row valid even
        ///     when the layout is radial or otherwise non-tabular.
        /// </summary>
        public BeamGridReferences ResolveReferences(Point2 startPlan, Point2 endPlan)
        {
            var finder = new NearestGridFinder(_tolerance);
            var axisStart = finder.Find(_registry, GeometryAxisFamily.X, startPlan);
            var axisEnd = finder.Find(_registry, GeometryAxisFamily.X, endPlan);
            // The "axis" grid is the X family at start; the "to" grid is the
            // X family at end. The Y family is the "from" reference. Skew or
            // radial families collapse to null so the schema can represent
            // the rows honestly.
            return new BeamGridReferences
            {
                AxisGridStart = ToReference(axisStart),
                AxisGridEnd = ToReference(axisEnd),
                FromGrid = ToReference(finder.Find(_registry, GeometryAxisFamily.Y, startPlan)),
                ToGrid = ToReference(finder.Find(_registry, GeometryAxisFamily.Y, endPlan)),
            };
        }

        private static GridReference ToReference(NearestGridHit hit)
        {
            if (!hit.IsHit || hit.Grid == null) return null;
            return new GridReference
            {
                GridUniqueId = hit.Grid.UniqueId,
                GridName = hit.Grid.Name,
                SignedOffsetMm = hit.SignedOffsetMm,
                DistanceMm = hit.DistanceMm,
            };
        }
    }

    /// <summary>
    ///     The four grid references embedded in a beam record. Each may be
    ///     null when the layout does not align with the corresponding axis.
    /// </summary>
    public sealed class BeamGridReferences
    {
        public GridReference AxisGridStart { get; set; }
        public GridReference AxisGridEnd { get; set; }
        public GridReference FromGrid { get; set; }
        public GridReference ToGrid { get; set; }
    }
}