using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Commands;
using RevitMCPCommandSet.Models.Common;
using System.IO;
using System.Reflection;

namespace RevitMCPCommandSet.Utils
{
    public static class ProjectUtils
    {
        /// <summary>
        /// General-purpose method for creating a family instance
        /// </summary>
        /// <param name="doc">Current document</param>
        /// <param name="familySymbol">Family type</param>
        /// <param name="locationPoint">Location point</param>
        /// <param name="locationLine">Location line</param>
        /// <param name="baseLevel">Base level</param>
        /// <param name="topLevel">Second level (for TwoLevelsBased)</param>
        /// <param name="baseOffset">Base offset (ft)</param>
        /// <param name="topOffset">Top offset (ft)</param>
        /// <param name="faceDirection">Reference direction</param>
        /// <param name="handDirection">Reference direction</param>
        /// <param name="view">View</param>
        /// <returns>The created family instance, or null on failure</returns>
        public static FamilyInstance CreateInstance(
            this Document doc,
            FamilySymbol familySymbol,
            XYZ locationPoint = null,
            Line locationLine = null,
            Level baseLevel = null,
            Level topLevel = null,
            double baseOffset = -1,
            double topOffset = -1,
            XYZ faceDirection = null,
            XYZ handDirection = null,
            View view = null,
            Element explicitHost = null,
            bool snapToHostCenter = true)
        {
            // Basic argument checks
            if (doc == null)
                throw new ArgumentNullException($"Required argument {typeof(Document)} {nameof(doc)} is missing!");
            if (familySymbol == null)
                throw new ArgumentNullException($"Required argument {typeof(FamilySymbol)} {nameof(familySymbol)} is missing!");

            // Activate the family symbol
            if (!familySymbol.IsActive)
                familySymbol.Activate();

            FamilyInstance instance = null;

            // Choose the creation method based on the family placement type
            switch (familySymbol.Family.FamilyPlacementType)
            {
                // Single-level-based families (e.g. Generic Model)
                case FamilyPlacementType.OneLevelBased:
                    if (locationPoint == null)
                        throw new ArgumentNullException($"Required argument {typeof(XYZ)} {nameof(locationPoint)} is missing!");
                    // With level information
                    if (baseLevel != null)
                    {
                        instance = doc.Create.NewFamilyInstance(
                            locationPoint,                  // Physical location where the instance will be placed
                            familySymbol,                   // FamilySymbol representing the type of instance to insert
                            baseLevel,                      // Level used as the object's base level
                            StructuralType.NonStructural);  // Structural type of the element, if structural
                    }
                    // Without level information
                    else
                    {
                        instance = doc.Create.NewFamilyInstance(
                            locationPoint,                  // Physical location where the instance will be placed
                            familySymbol,                   // FamilySymbol representing the type of instance to insert
                            StructuralType.NonStructural);  // Structural type of the element, if structural
                    }
                    break;

                // Single-level, host-based families (e.g. doors, windows)
                case FamilyPlacementType.OneLevelBasedHosted:
                    if (locationPoint == null)
                        throw new ArgumentNullException($"Required argument {typeof(XYZ)} {nameof(locationPoint)} is missing!");

                    Element host = explicitHost;
                    XYZ placementPoint = locationPoint;

                    // If explicit host provided and it's a wall, snap to its centerline
                    if (host != null && snapToHostCenter && host is Wall explicitWall)
                    {
                        LocationCurve eLoc = explicitWall.Location as LocationCurve;
                        if (eLoc != null)
                        {
                            IntersectionResult eIr = eLoc.Curve.Project(locationPoint);
                            if (eIr != null)
                                placementPoint = new XYZ(eIr.XYZPoint.X, eIr.XYZPoint.Y, locationPoint.Z);
                        }
                    }

                    // Auto-detect host wall if not explicitly provided
                    if (host == null)
                    {
                        // Try geometric wall-centerline proximity first
                        var wallResult = doc.GetNearestWallByLocationLine(locationPoint, baseLevel);
                        if (wallResult.HasValue)
                        {
                            host = wallResult.Value.wall;
                            if (snapToHostCenter)
                                placementPoint = wallResult.Value.projectedPoint;
                        }
                        else
                        {
                            // Fall back to original ray-casting method
                            host = doc.GetNearestHostElement(locationPoint, familySymbol);
                        }
                    }

                    if (host == null)
                        throw new ArgumentNullException($"No valid host found!");

                    if (baseLevel != null)
                    {
                        instance = doc.Create.NewFamilyInstance(
                            placementPoint,
                            familySymbol,
                            host,
                            baseLevel,
                            StructuralType.NonStructural);
                    }
                    else
                    {
                        instance = doc.Create.NewFamilyInstance(
                            placementPoint,
                            familySymbol,
                            host,
                            StructuralType.NonStructural);
                    }

                    // Set sill height for windows (baseOffset maps to sill height for hosted elements)
                    if (instance != null && baseOffset != -1)
                    {
                        Parameter sillParam = instance.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                        if (sillParam != null && !sillParam.IsReadOnly)
                        {
                            sillParam.Set(baseOffset);
                        }
                    }
                    break;

                // Two-level-based families (e.g. columns)
                case FamilyPlacementType.TwoLevelsBased:
                    if (locationPoint == null)
                        throw new ArgumentNullException($"Required argument {typeof(XYZ)} {nameof(locationPoint)} is missing!");
                    if (baseLevel == null)
                        throw new ArgumentNullException($"Required argument {typeof(Level)} {nameof(baseLevel)} is missing!");
                    // Determine whether it is a structural or architectural column
                    StructuralType structuralType = StructuralType.NonStructural;
                    if (familySymbol.Category.Id.GetIntValue() == (int)BuiltInCategory.OST_StructuralColumns)
                        structuralType = StructuralType.Column;
                    instance = doc.Create.NewFamilyInstance(
                        locationPoint,              // Physical location where the instance will be placed
                        familySymbol,               // FamilySymbol representing the type of instance to insert
                        baseLevel,                  // Level used as the object's base level
                        structuralType);            // Structural type of the element, if structural
                    // Set base level, top level, base offset and top offset
                    if (instance != null)
                    {
                        // Set the column base level and top level
                        if (baseLevel != null)
                        {
                            Parameter baseLevelParam = instance.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM);
                            if (baseLevelParam != null)
                                baseLevelParam.Set(baseLevel.Id);
                        }
                        if (topLevel != null)
                        {
                            Parameter topLevelParam = instance.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM);
                            if (topLevelParam != null)
                                topLevelParam.Set(topLevel.Id);
                        }
                        // Get the base offset parameter
                        if (baseOffset != -1)
                        {
                            Parameter baseOffsetParam = instance.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM);
                            if (baseOffsetParam != null && baseOffsetParam.StorageType == StorageType.Double)
                            {
                                // Convert millimeters to Revit internal units
                                double baseOffsetInternal = baseOffset;
                                baseOffsetParam.Set(baseOffsetInternal);
                            }
                        }
                        // Get the top offset parameter
                        if (topOffset != -1)
                        {
                            Parameter topOffsetParam = instance.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM);
                            if (topOffsetParam != null && topOffsetParam.StorageType == StorageType.Double)
                            {
                                // Convert millimeters to Revit internal units
                                double topOffsetInternal = topOffset;
                                topOffsetParam.Set(topOffsetInternal);
                            }
                        }
                    }
                    break;

                // View-specific families (e.g. detail annotations)
                case FamilyPlacementType.ViewBased:
                    if (locationPoint == null)
                        throw new ArgumentNullException($"Required argument {typeof(XYZ)} {nameof(locationPoint)} is missing!");
                    instance = doc.Create.NewFamilyInstance(
                        locationPoint,  // Origin of the family instance. If created on a ViewPlan, the origin is projected onto the plan view
                        familySymbol,   // FamilySymbol representing the type of instance to insert
                        view);          // 2D view in which to place the family instance
                    break;

                // Work-plane-based families (e.g. face-based Generic Model, including face-based, wall-based, etc.)
                case FamilyPlacementType.WorkPlaneBased:
                    if (locationPoint == null)
                        throw new ArgumentNullException($"Required argument {typeof(XYZ)} {nameof(locationPoint)} is missing!");
                    // Get the nearest host face
                    Reference hostFace = doc.GetNearestFaceReference(locationPoint, 1000 / 304.8);
                    if (hostFace == null)
                        throw new ArgumentNullException($"No valid host found!");
                    if (faceDirection == null || faceDirection == XYZ.Zero)
                    {
                        var result = doc.GenerateDefaultOrientation(hostFace);
                        faceDirection = result.FacingOrientation;
                    }
                    // Create the family instance on the face using the point and direction
                    instance = doc.Create.NewFamilyInstance(
                        hostFace,               // Reference to the face  
                        locationPoint,          // Point on the face where the instance will be placed
                        faceDirection,          // Vector defining the instance orientation. It defines the rotation of the instance on the face, so it cannot be parallel to the face normal
                        familySymbol);          // FamilySymbol representing the type of instance to insert. It must be a family whose FamilyPlacementType is WorkPlaneBased
                    break;

                // Line-based families on a work plane (e.g. line-based Generic Model)
                case FamilyPlacementType.CurveBased:
                    if (locationLine == null)
                        throw new ArgumentNullException($"Required argument {typeof(Line)} {nameof(locationLine)} is missing!");

                    // Get the nearest host face (no tolerance allowed)
                    Reference lineHostFace = doc.GetNearestFaceReference(locationLine.Evaluate(0.5, true), 1e-5);
                    if (lineHostFace != null)
                    {
                        instance = doc.Create.NewFamilyInstance(
                            lineHostFace,   // Reference to the face 
                            locationLine,   // Curve the family instance is based on
                            familySymbol);  // FamilySymbol representing the type of instance to insert. It must be a family whose FamilyPlacementType is WorkPlaneBased or CurveBased
                    }
                    else
                    {
                        instance = doc.Create.NewFamilyInstance(
                            locationLine,                   // Curve the family instance is based on
                            familySymbol,                   // FamilySymbol representing the type of instance to insert. It must be a family whose FamilyPlacementType is WorkPlaneBased or CurveBased
                            baseLevel,                      // Level used as the base level of the object
                            StructuralType.NonStructural);  // Structural type of the element, if structural
                    }
                    if (instance != null)
                    {
                        // Get the base offset parameter
                        if (baseOffset != -1)
                        {
                            Parameter baseOffsetParam = instance.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
                            if (baseOffsetParam != null && baseOffsetParam.StorageType == StorageType.Double)
                            {
                                // Convert millimeters to Revit internal units
                                double baseOffsetInternal = baseOffset;
                                baseOffsetParam.Set(baseOffsetInternal);
                            }
                        }
                    }
                    break;

                // Line-based, view-specific families (e.g. detail components)
                case FamilyPlacementType.CurveBasedDetail:
                    if (locationLine == null)
                        throw new ArgumentNullException($"Required argument {typeof(Line)} {nameof(locationLine)} is missing!");
                    if (view == null)
                        throw new ArgumentNullException($"Required argument {typeof(View)} {nameof(view)} is missing!");
                    instance = doc.Create.NewFamilyInstance(
                        locationLine,   // Line location of the family instance. The line must lie in the view plane
                        familySymbol,   // FamilySymbol representing the type of instance to insert
                        view);          // 2D view in which to place the family instance
                    break;

                // Structural curve-driven families (e.g. beams, braces or slanted columns)
                case FamilyPlacementType.CurveDrivenStructural:
                    if (locationLine == null)
                        throw new ArgumentNullException($"Required argument {typeof(Line)} {nameof(locationLine)} is missing!");
                    if (baseLevel == null)
                        throw new ArgumentNullException($"Required argument {typeof(Level)} {nameof(baseLevel)} is missing!");
                    instance = doc.Create.NewFamilyInstance(
                        locationLine,                   // Curve the family instance is based on
                        familySymbol,                   // FamilySymbol representing the type of instance to insert. It must be a family whose FamilyPlacementType is WorkPlaneBased or CurveBased
                        baseLevel,                      // Level used as the base level of the object
                        StructuralType.Beam);           // Structural type of the element, if structural
                    break;

                // Adaptive families (e.g. Adaptive Generic Model, curtain panels)
                case FamilyPlacementType.Adaptive:
                    throw new NotImplementedException("Creation for FamilyPlacementType.Adaptive is not implemented!");

                default:
                    break;
            }
            return instance;
        }

        /// <summary>
        /// Generates the default facing and hand orientations (by default the long side is HandOrientation and the short side is FacingOrientation)
        /// </summary>
        /// <param name="hostFace"></param>
        /// <returns></returns>
        public static (XYZ FacingOrientation, XYZ HandOrientation) GenerateDefaultOrientation(this Document doc, Reference hostFace)
        {
            var facingOrientation = new XYZ();  // Facing orientation: direction of the family's +Y axis after loading
            var handOrientation = new XYZ();    // Hand orientation: direction of the family's +X axis after loading

            // Step1 Get the face object from the Reference
            Face face = doc.GetElement(hostFace.ElementId).GetGeometryObjectFromReference(hostFace) as Face;

            // Step2 Get the face profile
            List<Curve> profile = null;
            // Profile curves; each sublist is a closed loop, the first is usually the outer loop
            List<List<Curve>> profiles = new List<List<Curve>>();
            // Get all edge loops (outer loop and any inner holes)
            EdgeArrayArray edgeLoops = face.EdgeLoops;
            // Iterate over each edge loop
            foreach (EdgeArray loop in edgeLoops)
            {
                List<Curve> currentLoop = new List<Curve>();
                // Get each edge in the loop
                foreach (Edge edge in loop)
                {
                    Curve curve = edge.AsCurve();
                    currentLoop.Add(curve);
                }
                // Add the loop to the results if it has edges
                if (currentLoop.Count > 0)
                {
                    profiles.Add(currentLoop);
                }
            }
            // The first loop is usually the outer loop
            if (profiles != null && profiles.Any())
                profile = profiles.FirstOrDefault();

            // Step3 Get the face normal
            XYZ faceNormal = null;
            // For a planar face, the normal can be read directly
            if (face is PlanarFace planarFace)
                faceNormal = planarFace.FaceNormal;

            // Step4 Get the two main directions of the face (satisfying the right-hand rule)
            var result = face.GetMainDirections();
            var primaryDirection = result.PrimaryDirection;
            var secondaryDirection = result.SecondaryDirection;

            // By default the long side is HandOrientation and the short side is FacingOrientation
            facingOrientation = primaryDirection;
            handOrientation = secondaryDirection;

            // Check the right-hand rule (thumb: HandOrientation, index finger: FacingOrientation, middle finger: FaceNormal)
            if (!facingOrientation.IsRightHandRuleCompliant(handOrientation, faceNormal))
            {
                var newHandOrientation = facingOrientation.GenerateIndexFinger(faceNormal);
                if (newHandOrientation != null)
                {
                    handOrientation = newHandOrientation;
                }
            }

            return (facingOrientation, handOrientation);
        }

        /// <summary>
        /// Gets the Reference of the face nearest to a point
        /// </summary>
        /// <param name="doc">Current document</param>
        /// <param name="location">Target point location</param>
        /// <param name="radius">Search radius (internal units)</param>
        /// <returns>Reference of the nearest face, or null if none is found</returns>
        public static Reference GetNearestFaceReference(this Document doc, XYZ location, double radius = 1000 / 304.8)
        {
            try
            {
                // Tolerance offset
                location = new XYZ(location.X, location.Y, location.Z + 0.1 / 304.8);

                // Create or get a 3D view
                View3D view3D = null;
                FilteredElementCollector collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D));

                foreach (View3D v in collector)
                {
                    if (!v.IsTemplate)
                    {
                        view3D = v;
                        break;
                    }
                }

                if (view3D == null)
                {
                    using (Transaction trans = new Transaction(doc, "Create 3D View"))
                    {
                        trans.Start();
                        ViewFamilyType vft = new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewFamilyType))
                            .Cast<ViewFamilyType>()
                            .FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);

                        if (vft != null)
                        {
                            view3D = View3D.CreateIsometric(doc, vft.Id);
                        }
                        trans.Commit();
                    }
                }

                if (view3D == null)
                {
                    TaskDialog.Show("Error", "Unable to create or get a 3D view");
                    return null;
                }

                // Rays in 6 directions
                XYZ[] directions = new XYZ[]
                {
                  XYZ.BasisX,    // +X
                  -XYZ.BasisX,   // -X
                  XYZ.BasisY,    // +Y
                  -XYZ.BasisY,   // -Y
                  XYZ.BasisZ,    // +Z
                  -XYZ.BasisZ    // -Z
                };

                // Create filters
                ElementClassFilter wallFilter = new ElementClassFilter(typeof(Wall));
                ElementClassFilter floorFilter = new ElementClassFilter(typeof(Floor));
                ElementClassFilter ceilingFilter = new ElementClassFilter(typeof(Ceiling));
                ElementClassFilter instanceFilter = new ElementClassFilter(typeof(FamilyInstance));

                // Combine filters
                LogicalOrFilter categoryFilter = new LogicalOrFilter(
                    new ElementFilter[] { wallFilter, floorFilter, ceilingFilter, instanceFilter });


                // 1. Simplest: a filter for all instance elements
                //ElementFilter filter = new ElementIsElementTypeFilter(true);

                // Create the reference intersector
                ReferenceIntersector refIntersector = new ReferenceIntersector(categoryFilter,
                    FindReferenceTarget.Face, view3D);
                refIntersector.FindReferencesInRevitLinks = true; // Also find faces in linked files

                double minDistance = double.MaxValue;
                Reference nearestFace = null;

                foreach (XYZ direction in directions)
                {
                    // Cast a ray from the current location
                    IList<ReferenceWithContext> references = refIntersector.Find(location, direction);

                    foreach (ReferenceWithContext rwc in references)
                    {
                        double distance = rwc.Proximity; // Distance to the face

                        // If within the search radius and closer
                        if (distance <= radius && distance < minDistance)
                        {
                            minDistance = distance;
                            nearestFace = rwc.GetReference();
                        }
                    }
                }

                return nearestFace;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Error getting the nearest face: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets the element nearest to a point that can act as a host
        /// </summary>
        /// <param name="doc">Current document</param>
        /// <param name="location">Target point location</param>
        /// <param name="familySymbol">Family type, used to determine the host type</param>
        /// <param name="radius">Search radius (internal units)</param>
        /// <returns>The nearest host element, or null if none is found</returns>
        public static Element GetNearestHostElement(this Document doc, XYZ location, FamilySymbol familySymbol, double radius = 5.0)
        {
            try
            {
                // Basic argument checks
                if (doc == null || location == null || familySymbol == null)
                    return null;

                // Get the family's hosting behavior parameter
                Parameter hostParam = familySymbol.Family.get_Parameter(BuiltInParameter.FAMILY_HOSTING_BEHAVIOR);
                int hostingBehavior = hostParam?.AsInteger() ?? 0;

                // Create or get a 3D view
                View3D view3D = null;
                FilteredElementCollector viewCollector = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D));
                foreach (View3D v in viewCollector)
                {
                    if (!v.IsTemplate)
                    {
                        view3D = v;
                        break;
                    }
                }

                if (view3D == null)
                {
                    using (Transaction trans = new Transaction(doc, "Create 3D View"))
                    {
                        trans.Start();
                        ViewFamilyType vft = new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewFamilyType))
                            .Cast<ViewFamilyType>()
                            .FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);

                        if (vft != null)
                        {
                            view3D = View3D.CreateIsometric(doc, vft.Id);
                        }
                        trans.Commit();
                    }
                }

                if (view3D == null)
                {
                    TaskDialog.Show("Error", "Unable to create or get a 3D view");
                    return null;
                }

                // Create a class filter based on the hosting behavior
                ElementFilter classFilter;
                switch (hostingBehavior)
                {
                    case 1: // Wall based
                        classFilter = new ElementClassFilter(typeof(Wall));
                        break;
                    case 2: // Floor based
                        classFilter = new ElementClassFilter(typeof(Floor));
                        break;
                    case 3: // Ceiling based
                        classFilter = new ElementClassFilter(typeof(Ceiling));
                        break;
                    case 4: // Roof based
                        classFilter = new ElementClassFilter(typeof(RoofBase));
                        break;
                    default:
                        return null; // Unsupported host type
                }

                // Rays in 6 directions
                XYZ[] directions = new XYZ[]
                {
                    XYZ.BasisX,    // +X
                    -XYZ.BasisX,   // -X
                    XYZ.BasisY,    // +Y
                    -XYZ.BasisY,   // -Y
                    XYZ.BasisZ,    // +Z
                    -XYZ.BasisZ    // -Z
                };

                // Create the reference intersector
                ReferenceIntersector refIntersector = new ReferenceIntersector(classFilter,
                    FindReferenceTarget.Element, view3D);
                refIntersector.FindReferencesInRevitLinks = true; // Also find elements in linked files

                double minDistance = double.MaxValue;
                Element nearestHost = null;

                foreach (XYZ direction in directions)
                {
                    // Cast a ray from the current location
                    IList<ReferenceWithContext> references = refIntersector.Find(location, direction);

                    foreach (ReferenceWithContext rwc in references)
                    {
                        double distance = rwc.Proximity; // Distance to the element

                        // If within the search radius and closer
                        if (distance <= radius && distance < minDistance)
                        {
                            minDistance = distance;
                            nearestHost = doc.GetElement(rwc.GetReference().ElementId);
                        }
                    }
                }

                return nearestHost;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Error getting the nearest host element: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Finds the nearest wall to a point using wall location-line distance calculation.
        /// More reliable than ray-casting for door/window placement.
        /// </summary>
        /// <param name="doc">Current Revit document</param>
        /// <param name="point">Target point (internal units, feet)</param>
        /// <param name="level">Level to filter walls on</param>
        /// <param name="tolerance">Extra tolerance beyond half wall width (feet). Default ~5mm.</param>
        /// <returns>Tuple of (wall, projectedPoint, wallDirection, distance) or null</returns>
        public static (Wall wall, XYZ projectedPoint, XYZ wallDirection, double distance)?
            GetNearestWallByLocationLine(
                this Document doc,
                XYZ point,
                Level level,
                double tolerance = 5.0 / 304.8)
        {
            if (doc == null || point == null || level == null)
                return null;

            // Collect all walls on the given level
            var walls = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Where(w =>
                {
                    Parameter baseLevelParam = w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
                    return baseLevelParam != null && baseLevelParam.AsElementId() == level.Id;
                })
                .ToList();

            Wall bestWall = null;
            XYZ bestProjection = null;
            XYZ bestDirection = null;
            double bestDistance = double.MaxValue;

            foreach (Wall wall in walls)
            {
                LocationCurve locCurve = wall.Location as LocationCurve;
                if (locCurve == null) continue;

                Curve curve = locCurve.Curve;
                if (curve == null) continue;

                // Use Curve.Project() which handles both lines and arcs
                IntersectionResult ir = curve.Project(new XYZ(point.X, point.Y, curve.GetEndPoint(0).Z));
                if (ir == null) continue;

                XYZ projectedPt = ir.XYZPoint;
                double distance = new XYZ(point.X - projectedPt.X, point.Y - projectedPt.Y, 0).GetLength();

                // Check if point is within half the wall width + tolerance
                double halfWidth = wall.Width / 2.0;
                if (distance <= halfWidth + tolerance && distance < bestDistance)
                {
                    bestDistance = distance;
                    bestWall = wall;
                    bestProjection = new XYZ(projectedPt.X, projectedPt.Y, point.Z);

                    // Compute wall direction from curve tangent at projected parameter
                    XYZ p0 = curve.GetEndPoint(0);
                    XYZ p1 = curve.GetEndPoint(1);
                    bestDirection = new XYZ(p1.X - p0.X, p1.Y - p0.Y, 0).Normalize();
                }
            }

            if (bestWall == null)
                return null;

            return (bestWall, bestProjection, bestDirection, bestDistance);
        }

        /// <summary>
        /// Highlights the specified face
        /// </summary>
        /// <param name="doc">Current document</param>
        /// <param name="faceRef">Reference of the face to highlight</param>
        /// <param name="duration">Highlight duration (ms), default 3000 ms</param>
        public static void HighlightFace(this Document doc, Reference faceRef)
        {
            if (faceRef == null) return;

            // Get the solid fill pattern
            FillPatternElement solidFill = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(x => x.GetFillPattern().IsSolidFill);

            if (solidFill == null)
            {
                TaskDialog.Show("Error", "Solid fill pattern not found");
                return;
            }

            // Create the highlight override settings
            OverrideGraphicSettings ogs = new OverrideGraphicSettings();
            ogs.SetSurfaceForegroundPatternColor(new Color(255, 0, 0)); // Red
            ogs.SetSurfaceForegroundPatternId(solidFill.Id);
            ogs.SetSurfaceTransparency(0); // Opaque

            // Apply the highlight
            doc.ActiveView.SetElementOverrides(faceRef.ElementId, ogs);
        }

        /// <summary>
        /// Extracts the two main direction vectors of a face
        /// </summary>
        /// <param name="face">Input face</param>
        /// <returns>Tuple containing the primary and secondary directions</returns>
        /// <exception cref="ArgumentNullException">Thrown when the face is null</exception>
        /// <exception cref="ArgumentException">Thrown when the face profile cannot form a valid shape</exception>
        /// <exception cref="InvalidOperationException">Thrown when no valid direction can be extracted</exception>
        public static (XYZ PrimaryDirection, XYZ SecondaryDirection) GetMainDirections(this Face face)
        {
            // 1. Validate arguments
            if (face == null)
                throw new ArgumentNullException(nameof(face), "Face cannot be null");

            // 2. Get the face normal, used later to compute a perpendicular vector if needed
            XYZ faceNormal = face.ComputeNormal(new UV(0.5, 0.5));

            // 3. Get the outer loop of the face
            EdgeArrayArray edgeLoops = face.EdgeLoops;
            if (edgeLoops.Size == 0)
                throw new ArgumentException("Face has no valid edge loops", nameof(face));

            // The first loop is usually the outer loop
            EdgeArray outerLoop = edgeLoops.get_Item(0);

            // 4. Compute the direction vector and length of each edge
            List<XYZ> edgeDirections = new List<XYZ>();  // Unit direction of each edge
            List<double> edgeLengths = new List<double>(); // Length of each edge

            foreach (Edge edge in outerLoop)
            {
                Curve curve = edge.AsCurve();
                XYZ startPoint = curve.GetEndPoint(0);
                XYZ endPoint = curve.GetEndPoint(1);

                // Vector from start point to end point
                XYZ direction = endPoint - startPoint;
                double length = direction.GetLength();

                // Ignore edges that are too short (coincident vertices or numerical precision issues)
                if (length > 1e-10)
                {
                    edgeDirections.Add(direction.Normalize());  // Store the normalized direction
                    edgeLengths.Add(length);                    // Store the edge length
                }
            }

            if (edgeDirections.Count < 4) // Require at least 4 edges
            {
                throw new ArgumentException("The face does not have enough edges to form a valid shape", nameof(face));
            }

            // 5. Group edges with similar directions
            List<List<int>> directionGroups = new List<List<int>>();  // Direction groups; each group holds edge indices

            for (int i = 0; i < edgeDirections.Count; i++)
            {
                bool foundGroup = false;
                XYZ currentDirection = edgeDirections[i];

                // Try to add the current edge to an existing direction group
                for (int j = 0; j < directionGroups.Count; j++)
                {
                    var group = directionGroups[j];
                    // Compute the weighted average direction of the group
                    XYZ groupAvgDir = CalculateWeightedAverageDirection(group, edgeDirections, edgeLengths);

                    // Check whether the current direction is similar to the group average (either sign)
                    double dotProduct = Math.Abs(groupAvgDir.DotProduct(currentDirection));
                    if (dotProduct > 0.8) // Deviation within about 30 degrees counts as similar
                    {
                        group.Add(i);  // Add the current edge index to this group
                        foundGroup = true;
                        break;
                    }
                }

                // Create a new group if the edge matches no existing group
                if (!foundGroup)
                {
                    List<int> newGroup = new List<int> { i };
                    directionGroups.Add(newGroup);
                }
            }

            // 6. Compute each group's total weight (sum of edge lengths) and average direction
            List<double> groupWeights = new List<double>();
            List<XYZ> groupDirections = new List<XYZ>();

            foreach (var group in directionGroups)
            {
                // Sum the lengths of all edges in the group
                double totalLength = 0;
                foreach (int edgeIndex in group)
                {
                    totalLength += edgeLengths[edgeIndex];
                }
                groupWeights.Add(totalLength);

                // Compute the group's weighted average direction
                groupDirections.Add(CalculateWeightedAverageDirection(group, edgeDirections, edgeLengths));
            }

            // 7. Sort by weight to extract the main directions
            int[] sortedIndices = Enumerable.Range(0, groupDirections.Count)
                .OrderByDescending(i => groupWeights[i])
                .ToArray();

            // 8. Build the result
            if (groupDirections.Count >= 2)
            {
                // At least two groups: use the two heaviest as primary and secondary directions
                int primaryIndex = sortedIndices[0];
                int secondaryIndex = sortedIndices[1];

                return (
                    PrimaryDirection: groupDirections[primaryIndex],      // Primary direction
                    SecondaryDirection: groupDirections[secondaryIndex]   // Secondary direction
                );
            }
            else if (groupDirections.Count == 1)
            {
                // Only one group: build a secondary direction perpendicular to the primary
                XYZ primaryDirection = groupDirections[0];
                // Cross the face normal with the primary direction to get a perpendicular vector
                XYZ secondaryDirection = faceNormal.CrossProduct(primaryDirection).Normalize();

                return (
                    PrimaryDirection: primaryDirection,         // Primary direction 
                    SecondaryDirection: secondaryDirection      // Constructed perpendicular secondary direction
                );
            }
            else
            {
                // No valid direction could be extracted (rare)
                throw new InvalidOperationException("Unable to extract a valid direction from the face");
            }
        }

        /// <summary>
        /// Computes the length-weighted average direction of a group of edges
        /// </summary>
        /// <param name="edgeIndices">List of edge indices</param>
        /// <param name="directions">Direction vectors of all edges</param>
        /// <param name="lengths">Lengths of all edges</param>
        /// <returns>Normalized weighted average direction vector</returns>
        public static XYZ CalculateWeightedAverageDirection(List<int> edgeIndices, List<XYZ> directions, List<double> lengths)
        {
            if (edgeIndices.Count == 0)
                return null;

            double sumX = 0, sumY = 0, sumZ = 0;
            XYZ referenceDir = directions[edgeIndices[0]];  // Use the first direction in the group as reference

            foreach (int i in edgeIndices)
            {
                XYZ currentDir = directions[i];

                // Dot product with the reference direction to decide whether to flip
                double dot = referenceDir.DotProduct(currentDir);

                // If the direction is opposite (negative dot product), flip it before accumulating
                // This keeps vectors in a group aligned so they do not cancel out
                double factor = (dot >= 0) ? lengths[i] : -lengths[i];

                // Accumulate weighted vector components
                sumX += currentDir.X * factor;
                sumY += currentDir.Y * factor;
                sumZ += currentDir.Z * factor;
            }

            // Build the resulting vector and normalize it
            XYZ avgDir = new XYZ(sumX, sumY, sumZ);
            double magnitude = avgDir.GetLength();

            // Guard against a zero vector
            if (magnitude < 1e-10)
                return referenceDir;  // Fall back to the reference direction

            return avgDir.Normalize();  // Return the normalized direction vector
        }

        /// <summary>
        /// Checks whether three vectors are mutually perpendicular and satisfy the right-hand rule
        /// </summary>
        /// <param name="thumb">Thumb direction vector</param>
        /// <param name="indexFinger">Index finger direction vector</param>
        /// <param name="middleFinger">Middle finger direction vector</param>
        /// <param name="tolerance">Tolerance, default 1e-6</param>
        /// <returns>True if the vectors are mutually perpendicular and satisfy the right-hand rule; otherwise false</returns>
        public static bool IsRightHandRuleCompliant(this XYZ thumb, XYZ indexFinger, XYZ middleFinger, double tolerance = 1e-6)
        {
            // Check that the vectors are mutually perpendicular (all dot products near 0)
            double dotThumbIndex = Math.Abs(thumb.DotProduct(indexFinger));
            double dotThumbMiddle = Math.Abs(thumb.DotProduct(middleFinger));
            double dotIndexMiddle = Math.Abs(indexFinger.DotProduct(middleFinger));

            bool areOrthogonal = (dotThumbIndex <= tolerance) &&
                                  (dotThumbMiddle <= tolerance) &&
                                  (dotIndexMiddle <= tolerance);

            // Only check the right-hand rule if the vectors are mutually perpendicular
            if (!areOrthogonal)
                return false;

            // Dot the cross product with the thumb to test the right-hand rule
            XYZ crossProduct = indexFinger.CrossProduct(middleFinger);
            double rightHandTest = crossProduct.DotProduct(thumb);

            // A positive dot product means the right-hand rule holds
            return rightHandTest > tolerance;
        }

        /// <summary>
        /// Generates the index finger direction from the thumb and middle finger directions using the right-hand rule
        /// </summary>
        /// <param name="thumb">Thumb direction vector</param>
        /// <param name="middleFinger">Middle finger direction vector</param>
        /// <param name="tolerance">Perpendicularity tolerance, default 1e-6</param>
        /// <returns>The generated index finger direction, or null if the inputs are not perpendicular</returns>
        public static XYZ GenerateIndexFinger(this XYZ thumb, XYZ middleFinger, double tolerance = 1e-6)
        {
            // Normalize the input vectors first
            XYZ normalizedThumb = thumb.Normalize();
            XYZ normalizedMiddleFinger = middleFinger.Normalize();

            // Check that the two vectors are perpendicular (dot product near 0)
            double dotProduct = normalizedThumb.DotProduct(normalizedMiddleFinger);

            // If the absolute dot product exceeds the tolerance, they are not perpendicular
            if (Math.Abs(dotProduct) > tolerance)
            {
                return null;
            }

            // Compute the index finger direction via cross product and negate it
            XYZ indexFinger = normalizedMiddleFinger.CrossProduct(normalizedThumb).Negate();

            // Return the normalized index finger direction
            return indexFinger.Normalize();
        }

        /// <summary>
        /// Creates or gets a level at the specified elevation
        /// </summary>
        /// <param name="doc">Revit document</param>
        /// <param name="elevation">Level elevation (ft)</param>
        /// <param name="levelName">Level name</param>
        /// <returns></returns>
        public static Level CreateOrGetLevel(this Document doc, double elevation, string levelName)
        {
            // First look for an existing level at the given elevation
            Level existingLevel = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => Math.Abs(l.Elevation - elevation) < 0.1 / 304.8);

            if (existingLevel != null)
                return existingLevel;

            // Create a new level
            Level newLevel = Level.Create(doc, elevation);
            // Set the level name
            Level namesakeLevel = new FilteredElementCollector(doc)
                 .OfClass(typeof(Level))
                 .Cast<Level>()
                 .FirstOrDefault(l => l.Name == levelName);
            if (namesakeLevel != null)
            {
                levelName = $"{levelName}_{newLevel.Id.GetValue()}";
            }
            newLevel.Name = levelName;

            return newLevel;
        }

        /// <summary>
        /// Finds the level nearest to the given height
        /// </summary>
        /// <param name="doc">Current Revit document</param>
        /// <param name="height">Target height (Revit internal units)</param>
        /// <returns>The level nearest to the target height, or null if the document has no levels</returns>
        public static Level FindNearestLevel(this Document doc, double height)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc), "Document cannot be null");

            // Use a LINQ query to get the nearest level
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => Math.Abs(level.Elevation - height))
                .FirstOrDefault();
        }

        ///// <summary>
        ///// Refreshes the view with an optional delay
        ///// </summary>
        //public static void Refresh(this Document doc, int waitingTime = 0, bool allowOperation = true)
        //{
        //    UIApplication uiApp = new UIApplication(doc.Application);
        //    UIDocument uiDoc = uiApp.ActiveUIDocument;

        //    // Check whether the document is modifiable
        //    if (uiDoc.Document.IsModifiable)
        //    {
        //        // Regenerate the model
        //        uiDoc.Document.Regenerate();
        //    }
        //    // Refresh the UI
        //    uiDoc.RefreshActiveView();

        //    // Wait for the delay
        //    if (waitingTime != 0)
        //    {
        //        System.Threading.Thread.Sleep(waitingTime);
        //    }

        //    // Let the user perform non-safe operations
        //    if (allowOperation)
        //    {
        //        System.Windows.Forms.Application.DoEvents();
        //    }
        //}

        /// <summary>
        /// Saves the message to the specified file on the desktop (overwrites by default)
        /// </summary>
        /// <param name="message">Message content to save</param>
        /// <param name="fileName">Target file name</param>
        public static void SaveToDesktop(this string message, string fileName = "temp.json", bool isAppend = false)
        {
            // Ensure the file name has an extension
            if (!Path.HasExtension(fileName))
            {
                fileName += ".txt"; // Append .txt by default
            }

            // Get the desktop path
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

            // Build the full file path
            string filePath = Path.Combine(desktopPath, fileName);

            // Write the file (overwrite mode)
            using (StreamWriter sw = new StreamWriter(filePath, isAppend))
            {
                sw.WriteLine($"{message}");
            }
        }

    }
}
