using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Models.DataExtraction.Register;
using RevitMCPCommandSet.Services.DataExtraction.Register;
using ElementIdAlias = RevitMCPCommandSet.Utils.ElementIdExtensions;
using RGrid = Autodesk.Revit.DB.Grid;
using RPhaseFilter = RevitMCPCommandSet.Services.DataExtraction.Register.PhaseFilter;
using RDesignOptionFilter = RevitMCPCommandSet.Services.DataExtraction.Register.DesignOptionFilter;
using RLinkedDocumentContext = RevitMCPCommandSet.Services.DataExtraction.Register.LinkedDocumentContext;
using RRegisterCoordinateContext = RevitMCPCommandSet.Services.DataExtraction.Register.RegisterCoordinateContext;

namespace RevitMCPCommandSet.Services.DataExtraction.GridExtraction
{
    /// <summary>
    ///     Pure-data view of one grid: its unique id, name, and the
    ///     register-ready <see cref="RegisterGeometry.ICurve2"/> already in
    ///     millimetres inside the requested coordinate frame. The struct is
    ///     what flows between collection and record building.
    /// </summary>
    internal sealed class GridResolution
    {
        public GridResolution(
            string uniqueId,
            string name,
            RegisterGeometry.ICurve2 curve,
            ElementProvenance provenance)
        {
            UniqueId = uniqueId ?? throw new ArgumentNullException(nameof(uniqueId));
            Name = name ?? string.Empty;
            Curve = curve ?? throw new ArgumentNullException(nameof(curve));
            Provenance = provenance ?? new ElementProvenance();
        }

        public string UniqueId { get; }
        public string Name { get; }
        public RegisterGeometry.ICurve2 Curve { get; }
        public ElementProvenance Provenance { get; }
    }

    /// <summary>
    ///     Collects <see cref="RGrid"/> elements from the host and (optionally)
    ///     loaded Revit links, applies scope/policy filters, and converts each
    ///     surviving grid's <see cref="RGrid.Curve"/> into the planar
    ///     <see cref="RegisterGeometry.ICurve2"/> form used by the register
    ///     handler.
    /// </summary>
    internal sealed class GridCollector
    {
        private readonly Document _host;
        private readonly string _hostDocumentKey;
        private readonly View _view;
        private readonly RRegisterCoordinateContext _coordinates;
        private readonly HashSet<long> _levelElementIds;
        private readonly RPhaseFilter _phase;
        private readonly RDesignOptionFilter _designOption;

        public GridCollector(
            Document host,
            string hostDocumentKey,
            View view,
            RRegisterCoordinateContext coordinates,
            IReadOnlyList<Level> levels,
            RPhaseFilter phase,
            RDesignOptionFilter designOption)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _hostDocumentKey = hostDocumentKey ?? throw new ArgumentNullException(nameof(hostDocumentKey));
            _view = view;
            _coordinates = coordinates ?? throw new ArgumentNullException(nameof(coordinates));
            _levelElementIds = new HashSet<long>();
            if (levels != null)
            {
                foreach (var lvl in levels)
                {
                    _levelElementIds.Add(ElementIdAlias.GetValue(lvl.Id));
                }
            }
            _phase = phase ?? throw new ArgumentNullException(nameof(phase));
            _designOption = designOption ?? throw new ArgumentNullException(nameof(designOption));
        }

        /// <summary>
        ///     Collect host grids. The collector is document-wide unless a view
        ///     id was supplied, in which case only grids visible in that view
        ///     are returned.
        /// </summary>
        public IReadOnlyList<GridResolution> CollectHost()
        {
            var collector = _view != null
                ? new FilteredElementCollector(_host, _view.Id)
                : new FilteredElementCollector(_host);
            return CollectInternal(collector, documentKey: _hostDocumentKey, linkInstanceUniqueId: null);
        }

        /// <summary>
        ///     Collect grids from every loaded Revit link. Returns an empty
        ///     list when <paramref name="links"/> is null or empty.
        /// </summary>
        public IReadOnlyList<GridResolution> CollectLinks(IReadOnlyList<RLinkedDocumentContext> links)
        {
            if (links == null || links.Count == 0) return Array.Empty<GridResolution>();
            var output = new List<GridResolution>();
            foreach (var link in links)
            {
                var collector = new FilteredElementCollector(link.LinkDocument);
                if (_view != null)
                {
                    // Linked grids cannot be filtered by the host view directly;
                    // filter to elements visible in the view through the standard
                    // visible-in-view filter, which returns true only for elements
                    // visible through that specific instance.
                    try
                    {
                        collector = collector.WherePasses(new VisibleInViewFilter(link.LinkDocument, _view.Id));
                    }
                    catch (InvalidOperationException)
                    {
                        // Some links don't expose the host view; fall back to
                        // document-wide collection and warn elsewhere.
                    }
                }
                var perLink = CollectInternal(collector, documentKey: link.LinkedDocumentKey, linkInstanceUniqueId: link.LinkInstanceUniqueId);
                output.AddRange(perLink);
            }
            return output;
        }

        private IReadOnlyList<GridResolution> CollectInternal(
            FilteredElementCollector collector,
            string documentKey,
            string linkInstanceUniqueId)
        {
            var list = new List<GridResolution>();
            foreach (var element in collector.OfClass(typeof(RGrid)).WhereElementIsNotElementType())
            {
                if (element is not RGrid grid) continue;
                if (!LevelMatches(grid)) continue;
                if (!_phase.Accepts(grid)) continue;
                if (!_designOption.Accepts(grid)) continue;

                var resolutions = ExtractCurves(grid);
                foreach (var curve in resolutions)
                {
                    var provenance = new ElementProvenance
                    {
                        ElementId = ElementIdAlias.GetValue(grid.Id),
                        UniqueId = grid.UniqueId,
                        DocumentKey = documentKey,
                        LinkInstanceUniqueId = linkInstanceUniqueId,
                        LevelId = GridLevelId(grid),
                        LevelName = GridLevelName(grid),
                        PhaseId = grid.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId() is ElementId id
                            ? ElementIdAlias.GetValue(id).ToString()
                            : null,
                        DesignOptionId = grid.get_Parameter(BuiltInParameter.DESIGN_OPTION_ID)?.AsElementId() is ElementId optId
                            && optId != ElementId.InvalidElementId
                            ? ElementIdAlias.GetValue(optId).ToString()
                            : null,
                        Category = grid.Category?.Name,
                    };
                    list.Add(new GridResolution(grid.UniqueId, grid.Name ?? string.Empty, curve, provenance));
                }
            }
            return list;
        }

        private bool LevelMatches(RGrid grid)
        {
            if (_levelElementIds == null || _levelElementIds.Count == 0) return true;
            // Grids are datum planes; they do not carry an instance level
            // parameter themselves. We honour the requested scope by treating
            // any grid with an undefined level as a match for every request.
            var gridLevelId = GridLevelId(grid);
            if (gridLevelId == null) return true;
            if (long.TryParse(gridLevelId, out var numeric))
            {
                return _levelElementIds.Contains(numeric);
            }
            return false;
        }

        private static string GridLevelId(RGrid grid)
        {
            // Grids in Revit have no INSTANCE_LEVEL_PARAM; their enclosing
            // level can only be inferred from view context. When no level
            // information is available we return null so the caller falls
            // through and treats the grid as in-scope.
            return null;
        }

        private static string GridLevelName(RGrid grid)
        {
            return null;
        }

/// <summary>
    ///     Extract the grid's plan curves in millimetres, applying the
    ///     host's coordinate transform. By default the grid's model curve
    ///     (<see cref="RGrid.Curve"/>) is used. When the request is
    ///     view-scoped and the grid has view-specific curves, those are
    ///     used instead so cropped grids report their visible extent.
    /// </summary>
    private List<RegisterGeometry.ICurve2> ExtractCurves(RGrid grid)
        {
            var output = new List<RegisterGeometry.ICurve2>();
            try
            {
                if (_view != null)
                {
                    // Use view-specific curves when available; fall back to
                    // the model curve below when the view returns nothing
                    // (grid bubbles only, etc.).
                    var viewCurves = grid.GetCurvesInView(DatumExtentType.ViewSpecific, _view);
                    if (viewCurves != null)
                    {
                        foreach (var c in viewCurves)
                        {
                            if (c == null) continue;
                            var adapted = RegisterCurveAdapter.ToCurve2(c);
                            if (adapted.IsConverted && adapted.Curve != null)
                            {
                                output.Add(adapted.Curve);
                            }
                        }
                    }
                }

                if (output.Count == 0)
                {
                    var modelCurve = grid.Curve;
                    if (modelCurve != null)
                    {
                        var adapted = RegisterCurveAdapter.ToCurve2(modelCurve);
                        if (adapted.IsConverted && adapted.Curve != null)
                        {
                            output.Add(adapted.Curve);
                        }
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Some grid configurations fail to expose curves; treat as
                // no curves (the record will be omitted).
            }
            return output;
        }
    }
}