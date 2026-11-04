namespace SyncLane.AspNetCore;

/// <summary>How <see cref="SyncLaneEndpoints.MapSyncLane"/> maps the endpoints.</summary>
public sealed class SyncLaneEndpointOptions
{
    /// <summary>The header every repair must carry. A page on another site cannot send it without your CORS policy allowing it.</summary>
    public const string ActionHeader = "X-SyncLane-Action";

    /// <summary>Map only the reads and the stream, no repairs. For screens that should look but not touch.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>The OpenAPI tag the endpoints are listed under. Default <c>SyncLane</c>.</summary>
    public string Tag { get; set; } = "SyncLane";
}
