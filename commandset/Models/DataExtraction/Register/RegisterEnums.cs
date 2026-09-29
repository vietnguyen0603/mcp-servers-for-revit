using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction.Register
{
    /// <summary>
    ///     Origin coordinate frame used for all plan geometry in a register response.
    /// </summary>
    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum CoordinateSystem
    {
        /// <summary>
        ///     Host project's internal coordinate system (default).
        /// </summary>
        Project = 0,

        /// <summary>
        ///     Shared coordinates, applicable when link transformation is requested.
        /// </summary>
        Shared = 1,
    }

    /// <summary>
    ///     Policy applied to design-option membership when collecting elements.
    /// </summary>
    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum DesignOptionPolicy
    {
        /// <summary>
        ///     Only elements whose primary design option is the active option.
        /// </summary>
        Primary = 0,

        /// <summary>
        ///     Elements visible in the active design option of the active view.
        /// </summary>
        Active = 1,

        /// <summary>
        ///     All primary-design-option instances regardless of the active option.
        /// </summary>
        All = 2,
    }

    /// <summary>
    ///     Planar curve kind for grid and beam geometry.
    /// </summary>
    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum CurveKind
    {
        Line = 0,
        Arc = 1,
    }

    /// <summary>
    ///     High-level grid axis family.
    /// </summary>
    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum AxisFamily
    {
        /// <summary>Family not resolved; review direction/note fields.</summary>
        Unknown = 0,

        /// <summary>Planar X axis family (cartesian columns).</summary>
        X = 1,

        /// <summary>Planar Y axis family (cartesian rows).</summary>
        Y = 2,

        /// <summary>Non-orthogonal grids not aligned to X or Y.</summary>
        Skew = 3,

        /// <summary>Radial grids (arc + radial spokes).</summary>
        Radial = 4,
    }

    /// <summary>
    ///     Record kind for the column/wall register. Columns are emitted as their
    ///     own first-class kind instead of being forced into CW/SW semantics.
    /// </summary>
    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum ColumnWallRecordKind
    {
        Column = 0,
        WallLeg = 1,
    }

    /// <summary>
    ///     Planar shape classification for a wall leg or grouped wall core.
    /// </summary>
    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum WallShape
    {
        /// <summary>Shape classification was not possible.</summary>
        Unknown = 0,

        /// <summary>Single straight wall leg / planar rectangle.</summary>
        Planar = 1,

        /// <summary>Two legs joined at one corner.</summary>
        L = 2,

        /// <summary>Three legs joined at two corners (U or T branch).</summary>
        UorT = 3,

        /// <summary>Closed rectangular core (box / shaft).</summary>
        Box = 4,

        /// <summary>Polygon core with more than four legs.</summary>
        Polygon = 5,
    }

    /// <summary>
    ///     Support category reported on a beam support record.
    /// </summary>
    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum SupportCategory
    {
        Unknown = 0,
        Wall = 1,
        Column = 2,
        Beam = 3,
        /// <summary>Support element exists but its category is not wall/column/beam.</summary>
        Other = 4,
    }

    /// <summary>
    ///     How the contact face was established for a support record.
    /// </summary>
    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum SupportIntersectionMethod
    {
        /// <summary>Method was not determined.</summary>
        Unknown = 0,

        /// <summary>Solid intersection between beam and support produced a contact edge.</summary>
        SolidIntersection = 1,

        /// <summary>Beam axis was projected onto a support bounding solid.</summary>
        AxisProjection = 2,

        /// <summary>Nearest bounding-box face within tolerance; geometric confirmation weak.</summary>
        BoundsFallback = 3,
    }

    /// <summary>
    ///     Severity tag for a warning entry.
    /// </summary>
    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum WarningSeverity
    {
        Info = 0,
        Warning = 1,
        Error = 2,
    }
}
