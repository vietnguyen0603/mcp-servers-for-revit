using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using RevitMCPCommandSet.Models.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RevitMCPCommandSet.Services
{
    public class OperateElementEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
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
        /// Operation data (input)
        /// </summary>
        public OperationSetting OperationData { get; private set; }
        /// <summary>
        /// Execution result (output)
        /// </summary>
        public AIResult<string> Result { get; private set; }

        /// <summary>
        /// Set the operation parameters
        /// </summary>
        public void SetParameters(OperationSetting data)
        {
            OperationData = data;
            _resetEvent.Reset();
        }
        public void Execute(UIApplication uiapp)
        {
            uiApp = uiapp;

            try
            {
                bool result = ExecuteElementOperation(uiDoc, OperationData);

                Result = new AIResult<string>
                {
                    Success = true,
                    Message = $"Operation executed successfully",
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<string>
                {
                    Success = false,
                    Message = $"Error operating on elements: {ex.Message}",
                };
            }
            finally
            {
                _resetEvent.Set(); // Notify the waiting thread that the operation is complete
            }
        }

        /// <summary>
        /// Wait for the operation to complete
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
            return "Operate Elements";
        }

        /// <summary>
        /// Perform the element operation described by the operation settings
        /// </summary>
        /// <param name="uidoc">Current UI document</param>
        /// <param name="setting">Operation settings</param>
        /// <returns>Whether the operation succeeded</returns>
        public static bool ExecuteElementOperation(UIDocument uidoc, OperationSetting setting)
        {
            // Validate parameters
            if (uidoc == null || uidoc.Document == null || setting == null || setting.ElementIds == null ||
                (setting.ElementIds.Count == 0 && setting.Action.ToLower() != "resetisolate"))
                throw new Exception("Invalid parameters: document is null or no elements were specified");

            Document doc = uidoc.Document;

            // Convert int element IDs to ElementId
            ICollection<ElementId> elementIds = setting.ElementIds.Select(id => new ElementId(id)).ToList();

            // Parse the operation type
            ElementOperationType action;
            if (!Enum.TryParse(setting.Action, true, out action))
            {
                throw new Exception($"Unsupported operation type: {setting.Action}");
            }

            // Perform the operation based on its type
            switch (action)
            {
                case ElementOperationType.Select:
                    // Select elements
                    uidoc.Selection.SetElementIds(elementIds);
                    return true;

                case ElementOperationType.SelectionBox:
                    // Create a section box in a 3D view

                    // Check whether the active view is a 3D view
                    View3D targetView;

                    if (doc.ActiveView is View3D)
                    {
                        // The active view is 3D, so create the section box there
                        targetView = doc.ActiveView as View3D;
                    }
                    else
                    {
                        // The active view is not 3D, so look for the default 3D view
                        FilteredElementCollector collector = new FilteredElementCollector(doc);
                        collector.OfClass(typeof(View3D));

                        // Try to find the default 3D view or any other usable 3D view
                        targetView = collector
                            .Cast<View3D>()
                            .FirstOrDefault(v => !v.IsTemplate && !v.IsLocked && (v.Name.Contains("{3D}") || v.Name.Contains("Default 3D")));

                        if (targetView == null)
                        {
                            // No suitable 3D view found
                            throw new Exception("Could not find a suitable 3D view to create the section box");
                        }

                        // Activate the 3D view
                        uidoc.ActiveView = targetView;
                    }

                    // Compute the bounding box of the selected elements
                    BoundingBoxXYZ boundingBox = null;

                    foreach (ElementId id in elementIds)
                    {
                        Element elem = doc.GetElement(id);
                        BoundingBoxXYZ elemBox = elem.get_BoundingBox(null);

                        if (elemBox != null)
                        {
                            if (boundingBox == null)
                            {
                                boundingBox = new BoundingBoxXYZ
                                {
                                    Min = new XYZ(elemBox.Min.X, elemBox.Min.Y, elemBox.Min.Z),
                                    Max = new XYZ(elemBox.Max.X, elemBox.Max.Y, elemBox.Max.Z)
                                };
                            }
                            else
                            {
                                // Expand the bounding box to include the current element
                                boundingBox.Min = new XYZ(
                                    Math.Min(boundingBox.Min.X, elemBox.Min.X),
                                    Math.Min(boundingBox.Min.Y, elemBox.Min.Y),
                                    Math.Min(boundingBox.Min.Z, elemBox.Min.Z));

                                boundingBox.Max = new XYZ(
                                    Math.Max(boundingBox.Max.X, elemBox.Max.X),
                                    Math.Max(boundingBox.Max.Y, elemBox.Max.Y),
                                    Math.Max(boundingBox.Max.Z, elemBox.Max.Z));
                            }
                        }
                    }

                    if (boundingBox == null)
                    {
                        throw new Exception("Could not create a bounding box for the selected elements");
                    }

                    // Enlarge the bounding box so it is slightly bigger than the elements
                    double offset = 1.0; // 1 ft offset
                    boundingBox.Min = new XYZ(boundingBox.Min.X - offset, boundingBox.Min.Y - offset, boundingBox.Min.Z - offset);
                    boundingBox.Max = new XYZ(boundingBox.Max.X + offset, boundingBox.Max.Y + offset, boundingBox.Max.Z + offset);

                    // Enable and set the section box in the 3D view
                    using (Transaction trans = new Transaction(doc, "Create Section Box"))
                    {
                        trans.Start();
                        targetView.IsSectionBoxActive = true;
                        targetView.SetSectionBox(boundingBox);
                        trans.Commit();
                    }

                    // Center the view on the elements
                    uidoc.ShowElements(elementIds);
                    return true;

                case ElementOperationType.SetColor:
                    // Set elements to the specified color
                    using (Transaction trans = new Transaction(doc, "Set Element Color"))
                    {
                        trans.Start();
                        SetElementsColor(doc, elementIds, setting.ColorValue);
                        trans.Commit();
                    }
                    // Scroll to the elements so they are visible
                    uidoc.ShowElements(elementIds);
                    return true;


                case ElementOperationType.SetTransparency:
                    // Set element transparency in the active view
                    using (Transaction trans = new Transaction(doc, "Set Element Transparency"))
                    {
                        trans.Start();

                        // Create the graphic override settings
                        OverrideGraphicSettings overrideSettings = new OverrideGraphicSettings();

                        // Clamp transparency to the 0-100 range
                        int transparencyValue = Math.Max(0, Math.Min(100, setting.TransparencyValue));

                        // Set surface transparency
                        overrideSettings.SetSurfaceTransparency(transparencyValue);

                        // Apply the transparency override to each element
                        foreach (ElementId id in elementIds)
                        {
                            doc.ActiveView.SetElementOverrides(id, overrideSettings);
                        }

                        trans.Commit();
                    }
                    return true;

                case ElementOperationType.Delete:
                    // Delete elements (requires a transaction)
                    using (Transaction trans = new Transaction(doc, "Delete Elements"))
                    {
                        trans.Start();
                        doc.Delete(elementIds);
                        trans.Commit();
                    }
                    return true;

                case ElementOperationType.Hide:
                    // Hide elements (requires an active view and a transaction)
                    using (Transaction trans = new Transaction(doc, "Hide Elements"))
                    {
                        trans.Start();
                        doc.ActiveView.HideElements(elementIds);
                        trans.Commit();
                    }
                    return true;

                case ElementOperationType.TempHide:
                    // Temporarily hide elements (requires an active view and a transaction)
                    using (Transaction trans = new Transaction(doc, "Temporarily Hide Elements"))
                    {
                        trans.Start();
                        doc.ActiveView.HideElementsTemporary(elementIds);
                        trans.Commit();
                    }
                    return true;

                case ElementOperationType.Isolate:
                    // Isolate elements (requires an active view and a transaction)
                    using (Transaction trans = new Transaction(doc, "Isolate Elements"))
                    {
                        trans.Start();
                        doc.ActiveView.IsolateElementsTemporary(elementIds);
                        trans.Commit();
                    }
                    return true;

                case ElementOperationType.Unhide:
                    // Unhide elements (requires an active view and a transaction)
                    using (Transaction trans = new Transaction(doc, "Unhide Elements"))
                    {
                        trans.Start();
                        doc.ActiveView.UnhideElements(elementIds);
                        trans.Commit();
                    }
                    return true;

                case ElementOperationType.ResetIsolate:
                    // Reset isolation (requires an active view and a transaction)
                    using (Transaction trans = new Transaction(doc, "Reset Isolation"))
                    {
                        trans.Start();
                        doc.ActiveView.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                        trans.Commit();
                    }
                    return true;

                default:
                    throw new Exception($"Unsupported operation type: {setting.Action}");
            }
        }

        /// <summary>
        /// Override the specified elements with the given color in the active view
        /// </summary>
        /// <param name="doc">Document</param>
        /// <param name="elementIds">IDs of the elements to color</param>
        /// <param name="elementColor">Color value (RGB)</param>
        private static void SetElementsColor(Document doc, ICollection<ElementId> elementIds, int[] elementColor)
        {
            // Validate the color array
            if (elementColor == null || elementColor.Length < 3)
            {
                elementColor = new int[] { 255, 0, 0 }; // Default to red
            }
            // Clamp RGB values to the 0-255 range
            int r = Math.Max(0, Math.Min(255, elementColor[0]));
            int g = Math.Max(0, Math.Min(255, elementColor[1]));
            int b = Math.Max(0, Math.Min(255, elementColor[2]));
            // Create the Revit color (cast to byte)
            Color color = new Color((byte)r, (byte)g, (byte)b);
            // Create the graphic override settings
            OverrideGraphicSettings overrideSettings = new OverrideGraphicSettings();
            // Apply the color
            overrideSettings.SetProjectionLineColor(color);
            overrideSettings.SetCutLineColor(color);
            overrideSettings.SetSurfaceForegroundPatternColor(color);
            overrideSettings.SetSurfaceBackgroundPatternColor(color);

            // Try to set the fill pattern
            try
            {
                // Collect fill patterns
                FilteredElementCollector patternCollector = new FilteredElementCollector(doc)
                    .OfClass(typeof(FillPatternElement));

                // Look for a solid fill pattern first
                FillPatternElement solidPattern = patternCollector
                    .Cast<FillPatternElement>()
                    .FirstOrDefault(p => p.GetFillPattern().IsSolidFill);

                if (solidPattern != null)
                {
                    overrideSettings.SetSurfaceForegroundPatternId(solidPattern.Id);
                    overrideSettings.SetSurfaceForegroundPatternVisible(true);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to set fill pattern: {ex.Message}");
            }

            // Apply the overrides to each element
            foreach (ElementId id in elementIds)
            {
                doc.ActiveView.SetElementOverrides(id, overrideSettings);
            }
        }

    }
}
