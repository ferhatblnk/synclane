namespace SyncLane.Cli;

/// <summary>Every message of the <c>synclane</c> command: what went wrong and how to fix it.</summary>
internal static class CliErrors
{
    public static SyncError CurlWithoutUrl() => new(
        CliErrorCodes.CurlWithoutUrl, SyncErrorKind.Validation,
        "No curl command with an http or https URL was given. Type 'synclane init' and paste the curl after it, as copied " +
        "from the API's documentation or the browser's \"Copy as cURL\", or pipe it in: pbpaste | synclane init.");

    public static SyncError ProbeRefused(int status) => new(
        CliErrorCodes.ProbeRefused, SyncErrorKind.Unavailable,
        $"The API answered {status} to the curl's request, so there was no answer to learn from. " + status switch
        {
            >= 300 and < 400 => "The URL redirects elsewhere, and recipes do not follow redirects: put the address it redirects to in the curl ('curl -v' shows it as Location).",
            401 or 403 => "The credential in the curl is missing, expired or not allowed; copy a fresh curl.",
            404 => "The URL does not exist; check it against the API's documentation.",
            429 => "The API asks to slow down; wait a moment and run the command again.",
            _ => "Run the curl itself to see what the API says."
        });

    public static SyncError ProbeNotJson(string contentType) => new(
        CliErrorCodes.ProbeNotJson, SyncErrorKind.Validation,
        $"The API answered with {contentType}, not JSON, and recipes read JSON. Check that the curl asks for JSON, for example with -H 'Accept: application/json'.");

    public static SyncError ProbeUnreachable(HttpRequestError error) => new(
        CliErrorCodes.ProbeUnreachable, SyncErrorKind.Unavailable,
        $"The curl's request did not reach the API: {Reason(error)}. Check the URL and the connection, then run the command again.");

    public static SyncError ProbeTimedOut(TimeSpan timeout) => new(
        CliErrorCodes.ProbeUnreachable, SyncErrorKind.Unavailable,
        $"The API did not answer the curl's request within {timeout.TotalSeconds:0} seconds. Run the curl itself to see whether it answers at all.");

    public static SyncError OutputExists(string file) => new(
        CliErrorCodes.OutputExists, SyncErrorKind.Conflict,
        $"'{file}' already exists, and synclane init does not replace a file unasked. Name another file with --out, or pass --force to replace it.");

    public static SyncError RecipeFileMissing(string file) => new(
        CliErrorCodes.RecipeFileMissing, SyncErrorKind.NotFound,
        $"There is no recipe file at '{file}'. Give the path of a recipe, such as one 'synclane init' wrote.");

    public static SyncError CredentialNotGiven(string credential, IEnumerable<string> variables) => new(
        CliErrorCodes.CredentialNotGiven, SyncErrorKind.Configuration,
        $"The recipe needs the credential '{credential}', and there is no terminal to ask for it in. " +
        $"Set {string.Join(" and ", variables)} in the environment.");

    public static SyncError SignedNotTestable(string recipe) => new(
        CliErrorCodes.SignedNotTestable, SyncErrorKind.Configuration,
        $"Recipe '{recipe}' signs its requests with an IRequestSigner of your application, which synclane test cannot load. " +
        "Run the job from your application instead, for example with EnqueueAsync and a test owner.");

    public static SyncError OwnerNeeded(string recipe) => new(
        CliErrorCodes.OwnerNeeded, SyncErrorKind.Validation,
        $"Recipe '{recipe}' uses the owner's values ({{{{owner.key}}}}), so the test needs one. Pass --owner with the key of an owner to test with.");

    public static SyncError PageRepeated(int page, string paging) => new(
        CliErrorCodes.PageRepeated, SyncErrorKind.Validation,
        $"Page {page} brought the same records as page {page - 1}: the API did not read the {paging}. " +
        "Check the parameter's name in the API's documentation and correct the recipe's pagination.");

    public static SyncError SinceInvalid(string value) => new(
        CliErrorCodes.SinceInvalid, SyncErrorKind.Validation,
        $"--since '{value}' is neither a length of time nor a date. Give a length such as 7d, 12h or 30m, or a date such as 2026-10-01.");

    private static string Reason(HttpRequestError error) => error switch
    {
        HttpRequestError.NameResolutionError => "the host name could not be resolved",
        HttpRequestError.ConnectionError => "the connection could not be made",
        HttpRequestError.SecureConnectionError => "the secure connection (TLS) could not be set up",
        HttpRequestError.ProxyTunnelError => "the proxy refused the connection",
        _ => $"the request failed ({error})"
    };
}
