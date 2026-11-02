namespace SyncLane.Cli;

/// <summary>
/// Every error code of the <c>synclane</c> command. A code is never reused or renumbered. <c>SL60xx</c> is a curl or a
/// recipe the command cannot work with.
/// </summary>
public static class CliErrorCodes
{
    /// <summary>The text given as a curl command has no http or https URL.</summary>
    public const string CurlWithoutUrl = "SL6001";

    /// <summary>The API refused the probe request, or failed to answer it.</summary>
    public const string ProbeRefused = "SL6002";

    /// <summary>The API answered the probe request with something that is not JSON.</summary>
    public const string ProbeNotJson = "SL6003";

    /// <summary>The probe request did not reach the API, or got no answer in time.</summary>
    public const string ProbeUnreachable = "SL6004";

    /// <summary>The file <c>synclane init</c> would write already exists.</summary>
    public const string OutputExists = "SL6005";

    /// <summary>The recipe file given to <c>synclane test</c> does not exist.</summary>
    public const string RecipeFileMissing = "SL6006";

    /// <summary>The recipe needs a credential that was neither in the environment nor typed in.</summary>
    public const string CredentialNotGiven = "SL6007";

    /// <summary>The recipe signs its requests with code of the application, which the command cannot run.</summary>
    public const string SignedNotTestable = "SL6008";

    /// <summary>The recipe uses the owner's values, and no owner was given.</summary>
    public const string OwnerNeeded = "SL6009";

    /// <summary>A page brought the same records as the page before it: the API did not read the paging parameter.</summary>
    public const string PageRepeated = "SL6010";

    /// <summary>The value of <c>--since</c> is neither a length of time nor a date.</summary>
    public const string SinceInvalid = "SL6011";
}
