namespace RevitMCPCommandSet.Models.DataExtraction.Register
{
    /// <summary>
    ///     Strongly-typed grid register response envelope.
    /// </summary>
    public class GridRegisterResponse : RegisterResponseEnvelope<GridRecord>
    {
    }

    /// <summary>
    ///     Strongly-typed column/wall register response envelope.
    /// </summary>
    public class ColumnWallRegisterResponse : RegisterResponseEnvelope<ColumnWallRecord>
    {
    }

    /// <summary>
    ///     Strongly-typed beam register response envelope.
    /// </summary>
    public class BeamRegisterResponse : RegisterResponseEnvelope<BeamRecord>
    {
    }
}
