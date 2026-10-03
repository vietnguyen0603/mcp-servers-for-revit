using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RevitMCPCommandSet.Models.Common
{
    /// <summary>
    /// Filter settings - supports combining multiple conditions
    /// </summary>
    public class FilterSetting
    {
        /// <summary>
        /// Gets or sets the Revit built-in category name to filter by (e.g. "OST_Walls").
        /// If null or empty, no category filtering is applied.
        /// </summary>
        [JsonProperty("filterCategory")]
        public string FilterCategory { get; set; } = null;
        /// <summary>
        /// Gets or sets the Revit element class name to filter by (e.g. "Wall" or "Autodesk.Revit.DB.Wall").
        /// If null or empty, no type filtering is applied.
        /// </summary>
        [JsonProperty("filterElementType")]
        public string FilterElementType { get; set; } = null;
        /// <summary>
        /// Gets or sets the ElementId value of the family type (FamilySymbol) to filter by.
        /// If 0 or negative, no family filtering is applied.
        /// Note: this filter applies only to element instances, not to element types.
        /// </summary>
        [JsonProperty("filterFamilySymbolId")]
        public int FilterFamilySymbolId { get; set; } = -1;
        /// <summary>
        /// Gets or sets whether to include element types (e.g. wall types, door types)
        /// </summary>
        [JsonProperty("includeTypes")]
        public bool IncludeTypes { get; set; } = false;
        /// <summary>
        /// Gets or sets whether to include element instances (e.g. placed walls, doors)
        /// </summary>
        [JsonProperty("includeInstances")]
        public bool IncludeInstances { get; set; } = true;
        /// <summary>
        /// Gets or sets whether to return only elements visible in the current view.
        /// Note: this filter applies only to element instances, not to element types.
        /// </summary>
        [JsonProperty("filterVisibleInCurrentView")]
        public bool FilterVisibleInCurrentView { get; set; }
        /// <summary>
        /// Gets or sets the minimum point of the bounding box filter (mm)
        /// If this and BoundingBoxMax are set, only elements intersecting the box are returned
        /// </summary>
        [JsonProperty("boundingBoxMin")]
        public JZPoint BoundingBoxMin { get; set; } = null;
        /// <summary>
        /// Gets or sets the maximum point of the bounding box filter (mm)
        /// If this and BoundingBoxMin are set, only elements intersecting the box are returned
        /// </summary>
        [JsonProperty("boundingBoxMax")]
        public JZPoint BoundingBoxMax { get; set; } = null;
        /// <summary>
        /// Maximum number of elements to return
        /// </summary>
        [JsonProperty("maxElements")]
        public int MaxElements { get; set; } = 50; 
        /// <summary>
        /// Validates the filter settings and checks for conflicts
        /// </summary>
        /// <returns>True if the settings are valid; otherwise false</returns>
        public bool Validate(out string errorMessage)
        {
            errorMessage = null;

            // At least one element kind must be selected
            if (!IncludeTypes && !IncludeInstances)
            {
                errorMessage = "Invalid filter settings: must include element types, element instances, or both";
                return false;
            }

            // At least one filter condition must be specified
            if (string.IsNullOrWhiteSpace(FilterCategory) &&
                string.IsNullOrWhiteSpace(FilterElementType) &&
                FilterFamilySymbolId <= 0)
            {
                errorMessage = "Invalid filter settings: at least one filter condition (category, element type, or family type) must be specified";
                return false;
            }

            // Check for filters that conflict with type-only filtering
            if (IncludeTypes && !IncludeInstances)
            {
                List<string> invalidFilters = new List<string>();
                if (FilterFamilySymbolId > 0)
                    invalidFilters.Add("family instance filter");
                if (FilterVisibleInCurrentView)
                    invalidFilters.Add("view visibility filter");
                if (invalidFilters.Count > 0)
                {
                    errorMessage = $"The following filters do not apply when filtering element types only: {string.Join(", ", invalidFilters)}";
                    return false;
                }
            }
            // Validate the bounding box filter
            if (BoundingBoxMin != null && BoundingBoxMax != null)
            {
                // Ensure the minimum point is less than or equal to the maximum point
                if (BoundingBoxMin.X > BoundingBoxMax.X ||
                    BoundingBoxMin.Y > BoundingBoxMax.Y ||
                    BoundingBoxMin.Z > BoundingBoxMax.Z)
                {
                    errorMessage = "Invalid bounding box filter: the minimum point must be less than or equal to the maximum point";
                    return false;
                }
            }
            else if (BoundingBoxMin != null || BoundingBoxMax != null)
            {
                errorMessage = "Invalid bounding box filter: both the minimum and maximum points must be set";
                return false;
            }
            return true;
        }
    }
}
