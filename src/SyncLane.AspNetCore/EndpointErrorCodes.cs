namespace SyncLane.AspNetCore;

/// <summary>
/// The codes of the HTTP endpoints, <c>SL7xxx</c>. They are public contract: never renumbered, reused or removed. Each is
/// listed with its cause and fix in docs/errors.md.
/// </summary>
public static class EndpointErrorCodes
{
    /// <summary>A request from another machine reached endpoints that do not require authorization.</summary>
    public const string RemoteWithoutAuthorization = "SL7001";

    /// <summary>A repair was asked for without the header that shows it came from a script or page of yours.</summary>
    public const string ActionHeaderMissing = "SL7002";
}
