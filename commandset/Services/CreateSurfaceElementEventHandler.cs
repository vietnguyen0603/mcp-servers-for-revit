using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class CreateSurfaceElementEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private UIApplication uiApp;
        private UIDocument uiDoc => uiApp.ActiveUIDocument;
        private Document doc => uiDoc.Document;
        private Autodesk.Revit.ApplicationServices.Application app => uiApp.Application;
        /// <summary>
        /// Event wait handle
        /// </summary>
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        /// <summary>
        /// Creation data (input)
        /// </summary>
        public List<SurfaceElement> CreatedInfo { get; private set; }
        /// <summary>
        /// Execution result (output)
        /// </summary>
        public AIResult<List<int>> Result { get; private set; }
        public string _floorName = "Generic - ";
        public bool _structural = true;
        private List<string> _warnings = new List<string>();

        /// <summary>
        /// Set the creation parameters
        /// </summary>
        public void SetParameters(List<SurfaceElement> data)
        {
            CreatedInfo = data;
            _resetEvent.Reset();
        }
        public void Execute(UIApplication uiapp)
        {
            uiApp = uiapp;

            try
            {
                var elementIds = new List<int>();
                _warnings.Clear();
                foreach (var data in CreatedInfo)
                {
                    int requestedTypeId = data.TypeId;
                    // Step 0: Resolve the element category
                    BuiltInCategory builtInCategory = BuiltInCategory.INVALID;
                    Enum.TryParse(data.Category.Replace(".", "").Replace("BuiltInCategory", ""), true, out builtInCategory);

                    // Step 1: Resolve levels and offsets
                    Level baseLevel = null;
                    Level topLevel = null;
                    double topOffset = -1;  // ft
                    double baseOffset = -1; // ft
                    baseLevel = doc.FindNearestLevel(data.BaseLevel / 304.8);
                    baseOffset = (data.BaseOffset + data.BaseLevel) / 304.8 - baseLevel.Elevation;
                    topLevel = doc.FindNearestLevel((data.BaseLevel + data.BaseOffset + data.Thickness) / 304.8);
                    topOffset = (data.BaseLevel + data.BaseOffset + data.Thickness) / 304.8 - topLevel.Elevation;
                    if (baseLevel == null)
                        continue;

                    // Step 2: Resolve the family type
                    FamilySymbol symbol = null;
                    FloorType floorType = null;
                    RoofType roofType = null;
                    CeilingType ceilingType = null;
                    if (data.TypeId != -1 && data.TypeId != 0)
                    {
                        ElementId typeELeId = new ElementId(data.TypeId);
                        if (typeELeId != null)
                        {
                            Element typeEle = doc.GetElement(typeELeId);
                            if (typeEle != null && typeEle is FamilySymbol)
                            {
                                symbol = typeEle as FamilySymbol;
                                // Convert the symbol's category to a BuiltInCategory value
                                builtInCategory = (BuiltInCategory)symbol.Category.Id.GetIntValue();
                            }
                            else if (typeEle != null && typeEle is FloorType)
                            {
                                floorType = typeEle as FloorType;
                                builtInCategory = (BuiltInCategory)floorType.Category.Id.GetIntValue();
                            }
                            else if (typeEle != null && typeEle is RoofType)
                            {
                                roofType = typeEle as RoofType;
                                builtInCategory = (BuiltInCategory)roofType.Category.Id.GetIntValue();
                            }
                            else if (typeEle != null && typeEle is CeilingType)
                            {
                                ceilingType = typeEle as CeilingType;
                                builtInCategory = (BuiltInCategory)ceilingType.Category.Id.GetIntValue();
                            }
                        }
                    }
                    if (builtInCategory == BuiltInCategory.INVALID)
                        continue;
                    switch (builtInCategory)
                    {
                        case BuiltInCategory.OST_Floors:
                            if (floorType == null && data.Thickness > 0)
                            {
                                // No usable typeId: get or create a floor type of the requested thickness
                                using (var typeTransaction = new Transaction(doc, "Create Floor Type"))
                                {
                                    typeTransaction.Start();
                                    floorType = CreateOrGetFloorType(doc, data.Thickness / 304.8);
                                    typeTransaction.Commit();
                                }
                                if (requestedTypeId != -1 && requestedTypeId != 0)
                                    _warnings.Add($"Requested floor typeId {requestedTypeId} not found. Used '{floorType.Name}' for thickness {data.Thickness} mm.");
                            }
                            else if (floorType != null && data.Thickness > 0)
                            {
                                var typeThickness = floorType.GetCompoundStructure()?.GetWidth() * 304.8 ?? 0;
                                if (Math.Abs(typeThickness - data.Thickness) > 1)
                                    _warnings.Add($"Floor type '{floorType.Name}' is {typeThickness:0} mm; requested thickness {data.Thickness} mm ignored because typeId was given.");
                            }
                            if (floorType == null)
                            {
                                // Requested typeId was invalid or not provided, fall back to first available
                                floorType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(FloorType))
                                    .OfCategory(BuiltInCategory.OST_Floors)
                                    .Cast<FloorType>()
                                    .FirstOrDefault();
                                if (floorType == null)
                                {
                                    _warnings.Add($"No floor types available in project.");
                                    continue;
                                }
                                if (requestedTypeId != -1 && requestedTypeId != 0)
                                {
                                    _warnings.Add($"Requested floor typeId {requestedTypeId} not found. Defaulted to '{floorType.Name}' (ID: {floorType.Id.GetIntValue()})");
                                }
                            }
                            break;
                        case BuiltInCategory.OST_Roofs:
                            if (roofType == null)
                            {
                                // Get default roof type if not specified
                                roofType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(RoofType))
                                    .OfCategory(BuiltInCategory.OST_Roofs)
                                    .Cast<RoofType>()
                                    .FirstOrDefault();
                                if (roofType == null)
                                {
                                    _warnings.Add($"No roof types available in project.");
                                    continue;
                                }
                                if (requestedTypeId != -1 && requestedTypeId != 0)
                                {
                                    _warnings.Add($"Requested roof typeId {requestedTypeId} not found. Defaulted to '{roofType.Name}' (ID: {roofType.Id.GetIntValue()})");
                                }
                            }
                            break;
                        case BuiltInCategory.OST_Ceilings:
                            if (ceilingType == null)
                            {
                                // Get default ceiling type if not specified
                                ceilingType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(CeilingType))
                                    .OfCategory(BuiltInCategory.OST_Ceilings)
                                    .Cast<CeilingType>()
                                    .FirstOrDefault();
                                if (ceilingType == null)
                                {
                                    _warnings.Add($"No ceiling types available in project.");
                                    continue;
                                }
                                if (requestedTypeId != -1 && requestedTypeId != 0)
                                {
                                    _warnings.Add($"Requested ceiling typeId {requestedTypeId} not found. Defaulted to '{ceilingType.Name}' (ID: {ceilingType.Id.GetIntValue()})");
                                }
                            }
                            break;
                        default:
                            if (symbol == null)
                            {
                                symbol = new FilteredElementCollector(doc)
                                    .OfClass(typeof(FamilySymbol))
                                    .OfCategory(builtInCategory)
                                    .Cast<FamilySymbol>()
                                    .FirstOrDefault(fs => fs.IsActive); // Use an active type as the default
                                if (symbol == null)
                                {
                                    symbol = new FilteredElementCollector(doc)
                                    .OfClass(typeof(FamilySymbol))
                                    .OfCategory(builtInCategory)
                                    .Cast<FamilySymbol>()
                                    .FirstOrDefault();
                                }
                            }
                            if (symbol == null)
                                continue;
                            break;
                    }

                    // Step 3: Create the floors
                    Floor floor = null;
                    using (Transaction transaction = new Transaction(doc, "Create Surface-Based Element"))
                    {
                        transaction.Start();

                        switch (builtInCategory)
                        {
                            case BuiltInCategory.OST_Floors:
                                CurveArray curves = new CurveArray();
                                foreach (var jzLine in data.Boundary.OuterLoop)
                                {
                                    curves.Append(JZLine.ToLine(jzLine));
                                }
                                CurveLoop curveLoop = CurveLoop.Create(data.Boundary.OuterLoop.Select(l => JZLine.ToLine(l) as Curve).ToList());

                                // Multi-version: Floor.Create introduced in Revit 2022 but stable in 2023+
#if REVIT2023_OR_GREATER
                                floor = Floor.Create(doc, new List<CurveLoop> { curveLoop }, floorType.Id, baseLevel.Id);
#else
                                floor = doc.Create.NewFloor(curves, floorType, baseLevel, data.Structural ?? false);
#endif
                                //Edit floor parameters
                                if (floor != null)
                                {
                                    floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(baseOffset);
                                    floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.Set((data.Structural ?? false) ? 1 : 0);
                                    elementIds.Add(floor.Id.GetIntValue());
                                }
                                break;
                            case BuiltInCategory.OST_Roofs:
                                CurveArray roofCurves = new CurveArray();
                                foreach (var jzLine in data.Boundary.OuterLoop)
                                {
                                    roofCurves.Append(JZLine.ToLine(jzLine));
                                }

                                ModelCurveArray modelCurves = new ModelCurveArray();
                                FootPrintRoof roof = doc.Create.NewFootPrintRoof(roofCurves, baseLevel, roofType, out modelCurves);

                                if (roof != null)
                                {
                                    // Set all edges to non-sloped for flat roof
                                    foreach (ModelCurve mc in modelCurves)
                                    {
                                        roof.set_DefinesSlope(mc, false);
                                    }
                                    // Set the roof offset from level
                                    Parameter offsetParam = roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM);
                                    if (offsetParam != null)
                                    {
                                        offsetParam.Set(baseOffset);
                                    }
                                    elementIds.Add(roof.Id.GetIntValue());
                                }
                                break;
                            case BuiltInCategory.OST_Ceilings:
                                CurveLoop ceilingCurveLoop = CurveLoop.Create(data.Boundary.OuterLoop.Select(l => JZLine.ToLine(l) as Curve).ToList());

#if REVIT2022_OR_GREATER
                                Ceiling ceiling = Ceiling.Create(doc, new List<CurveLoop> { ceilingCurveLoop }, ceilingType.Id, baseLevel.Id);
#else
                                // Ceiling.Create API not available before Revit 2022
                                Ceiling ceiling = null;
                                _warnings.Add("Ceiling creation is not supported in Revit versions before 2022.");
#endif
                                if (ceiling != null)
                                {
                                    // Set the ceiling height offset from level
                                    Parameter ceilingOffsetParam = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                                    if (ceilingOffsetParam != null)
                                    {
                                        ceilingOffsetParam.Set(baseOffset);
                                    }
                                    elementIds.Add(ceiling.Id.GetIntValue());
                                }
                                break;
                            default:
                                break;
                        }

                        transaction.Commit();
                    }
                }
                string message = $"Successfully created {elementIds.Count} element(s).";
                if (_warnings.Count > 0)
                {
                    message += "\n\n⚠ Warnings:\n  • " + string.Join("\n  • ", _warnings);
                }
                Result = new AIResult<List<int>>
                {
                    Success = true,
                    Message = message,
                    Response = elementIds,
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<List<int>>
                {
                    Success = false,
                    Message = $"Error creating surface-based elements: {ex.Message}",
                };
                TaskDialog.Show("Error", $"Error creating surface-based elements: {ex.Message}");
            }
            finally
            {
                _resetEvent.Set(); // Notify the waiting thread that the operation is complete
            }
        }

        /// <summary>
        /// Wait for creation to complete
        /// </summary>
        /// <param name="timeoutMilliseconds">Timeout in milliseconds</param>
        /// <returns>Whether the operation completed before the timeout</returns>
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        /// <summary>
        /// IExternalEventHandler.GetName implementation
        /// </summary>
        public string GetName()
        {
            return "Create Surface-Based Elements";
        }

        /// <summary>
        /// Get or create a floor type with the specified thickness
        /// </summary>
        /// <param name="thickness">Target thickness (ft)</param>
        /// <returns>Floor type matching the thickness</returns>
        private FloorType CreateOrGetFloorType(Document doc, double thickness = 200 / 304.8)
        {
            string name = $"{_floorName}{Math.Round(thickness * 304.8)}mm";
            FloorType existingType = new FilteredElementCollector(doc)
                                     .OfClass(typeof(FloorType))
                                     .OfCategory(BuiltInCategory.OST_Floors)
                                     .Cast<FloorType>()
                                     .FirstOrDefault(w => w.Name == name);
            if (existingType != null)
                return existingType;

            // Base: the type with the fewest layers (ideally a single concrete/generic layer)
            FloorType baseFloorType = new FilteredElementCollector(doc)
                                     .OfClass(typeof(FloorType))
                                     .OfCategory(BuiltInCategory.OST_Floors)
                                     .Cast<FloorType>()
                                     .Where(t => t.GetCompoundStructure() != null)
                                     .OrderBy(t => t.GetCompoundStructure().LayerCount)
                                     .ThenBy(t => t.Name.Contains("Generic") || t.Name.Contains("Concrete") ? 0 : 1)
                                     .FirstOrDefault()
                                     ?? throw new InvalidOperationException("No layered floor type available to duplicate.");

            FloorType newFloorType = baseFloorType.Duplicate(name) as FloorType;
            CompoundStructure cs = newFloorType.GetCompoundStructure();
            int core = Math.Max(cs.GetFirstCoreLayerIndex(), 0);
            double others = cs.GetWidth() - cs.GetLayerWidth(core);
            if (thickness - others <= 0.001)
                throw new ArgumentException($"Thickness {thickness * 304.8:0} mm is smaller than the base type's finish layers ({others * 304.8:0} mm).");
            cs.SetLayerWidth(core, thickness - others);
            newFloorType.SetCompoundStructure(cs);
            return newFloorType;
        }

    }
}
