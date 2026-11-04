namespace SyncLane.AspNetCore;

/// <summary>The one place where the endpoints word their errors.</summary>
internal static class EndpointErrors
{
    public static SyncError RemoteWithoutAuthorization() => new(
        EndpointErrorCodes.RemoteWithoutAuthorization, SyncErrorKind.Unauthorized,
        "The SyncLane endpoints answer only requests from this machine until they require authorization. Add " +
        ".RequireAuthorization(...) to MapSyncLane(...). A request that came through a proxy does not count as local.");

    public static SyncError ActionHeaderMissing(string header) => new(
        EndpointErrorCodes.ActionHeaderMissing, SyncErrorKind.Validation,
        $"A repair needs the header {header}, so that another site cannot make a browser send it. Send it with any value.");
}
