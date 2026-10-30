namespace SyncLane.Http;

/// <summary>
/// The one place where the HTTP source words its errors. Messages name the recipe and the field, never a header
/// value, a credential or a response body.
/// </summary>
internal static class HttpErrors
{
    // 50xx: the recipe cannot work as written. Found when it is loaded.

    public static SyncError RecipeJsonInvalid(string detail) => new(
        HttpErrorCodes.RecipeJsonInvalid, SyncErrorKind.Validation,
        $"The recipe could not be read: {detail} Check the JSON against the recipe format in docs/recipes.md.");

    public static SyncError FieldMissing(string recipe, string field) => new(
        HttpErrorCodes.FieldMissing, SyncErrorKind.Validation,
        $"Recipe '{recipe}': {field} is required and must not be empty.");

    public static SyncError UrlInvalid(string recipe, string reason) => new(
        HttpErrorCodes.UrlInvalid, SyncErrorKind.Validation,
        $"Recipe '{recipe}': request.url {reason}. Use an absolute https URL such as https://api.example.com/v1/orders.");

    public static SyncError PlaceholderUnknown(string recipe, string placeholder, string location) => new(
        HttpErrorCodes.PlaceholderUnknown, SyncErrorKind.Validation,
        $"Recipe '{recipe}': {location} uses {{{{{placeholder}}}}}, which does not exist. " +
        $"Available: {string.Join(", ", Placeholders.Known.Select(name => $"{{{{{name}}}}}"))}.");

    public static SyncError PlaceholderInHost(string recipe) => new(
        HttpErrorCodes.PlaceholderInHost, SyncErrorKind.Validation,
        $"Recipe '{recipe}': request.url has a placeholder before the path. The scheme, host and port must be written " +
        "out, so a recipe can only ever call the host it names.");

    public static SyncError RecordsPathInvalid(string recipe, string path) => new(
        HttpErrorCodes.RecordsPathInvalid, SyncErrorKind.Validation,
        $"Recipe '{recipe}': records '{path}' is not a supported path. Use $ for an array at the root, or $.name.name " +
        "with letters, digits, underscores and hyphens.");

    public static SyncError WindowFormatUnknown(string recipe, string format) => new(
        HttpErrorCodes.WindowFormatUnknown, SyncErrorKind.Validation,
        $"Recipe '{recipe}': window.format '{format}' is not known. Use iso8601, unix, unixMilliseconds or date.");

    public static SyncError MethodUnsupported(string recipe, string method) => new(
        HttpErrorCodes.MethodUnsupported, SyncErrorKind.Validation,
        $"Recipe '{recipe}': request.method '{method}' is not supported. Use GET or POST.");

    public static SyncError HeaderInvalid(string recipe, string header) => new(
        HttpErrorCodes.HeaderInvalid, SyncErrorKind.Validation,
        $"Recipe '{recipe}': header '{header}' is not valid. Names are HTTP tokens; values must not contain line breaks.");

    public static SyncError BodyNotAllowed(string recipe, string method) => new(
        HttpErrorCodes.BodyNotAllowed, SyncErrorKind.Validation,
        $"Recipe '{recipe}': request.body is set but request.method is {method}. Use POST to send a body, or remove it.");

    public static SyncError TemplateMalformed(string recipe, string location) => new(
        HttpErrorCodes.TemplateMalformed, SyncErrorKind.Validation,
        $"Recipe '{recipe}': {location} has an unclosed or empty placeholder. Write placeholders as {{{{window.start}}}}.");

    public static SyncError OptionInvalid(string setting, string rule) => new(
        HttpErrorCodes.OptionInvalid, SyncErrorKind.Configuration,
        $"AddSyncLaneHttp: {setting} {rule}.");

    public static SyncError SinkMissing(string type) => new(
        HttpErrorCodes.SinkMissing, SyncErrorKind.Configuration,
        $"The recipe for job type '{type}' has no sink. Add .WriteTo<TSink>() or .WriteTo((page, cancellationToken) => ...) after Recipe(...).");

    public static SyncError RecipeMissing(string queue, string type) => new(
        HttpErrorCodes.RecipeMissing, SyncErrorKind.Configuration,
        $"Job type '{type}' on queue '{queue}' runs with HttpRecipeHandler but has no recipe. Register one with " +
        $"http.Recipe(\"{type}\", ...) in AddSyncLaneHttp.");

    public static SyncError RecipeUnused(string type) => new(
        HttpErrorCodes.RecipeUnused, SyncErrorKind.Configuration,
        $"A recipe is registered for job type '{type}', but no queue declares '{type}' with HttpRecipeHandler. " +
        "Check the spelling, or declare the type with Snapshot, Windowed or Handle<HttpRecipeHandler>.");

    public static SyncError RecipeDeclaredTwice(string type) => new(
        HttpErrorCodes.RecipeDeclaredTwice, SyncErrorKind.Configuration,
        $"Two recipes are registered for job type '{type}'. Register one recipe per job type.");

    // 51xx: the recipe met a job or a response it does not fit. Found while running.

    public static SyncError WindowMissing(string recipe) => new(
        HttpErrorCodes.WindowMissing, SyncErrorKind.Configuration,
        $"Recipe '{recipe}' uses the job's window, but the job has none. Declare the job type with Windowed, or " +
        "remove {{window.start}} and {{window.end}} from the recipe.");

    public static SyncError RenderedValueInvalid(string recipe, string header) => new(
        HttpErrorCodes.RenderedValueInvalid, SyncErrorKind.Validation,
        $"Recipe '{recipe}': header '{header}' would contain a line break after its placeholders were filled. The " +
        "request was not sent. Check the owner key and credentials for line breaks.");

    public static SyncError RecordsNotFound(string recipe, string path) => new(
        HttpErrorCodes.RecordsNotFound, SyncErrorKind.Configuration,
        $"Recipe '{recipe}': the response has nothing at records '{path}'. Compare the path with a real response.");

    public static SyncError Throttled(string recipe) => new(
        HttpErrorCodes.Throttled, SyncErrorKind.Throttled,
        $"Recipe '{recipe}': the API answered 429 and asked to slow down. The job waits and continues from its cursor.");

    public static SyncError Unauthorized(string recipe, int status) => new(
        HttpErrorCodes.Unauthorized, SyncErrorKind.Unauthorized,
        $"Recipe '{recipe}': the API answered {status}. The credentials are wrong, expired or no longer allowed for this " +
        "owner. Fix the credentials, then requeue the job.");

    public static SyncError ServerError(string recipe, int status) => new(
        HttpErrorCodes.ServerError, SyncErrorKind.Unavailable,
        $"Recipe '{recipe}': the API answered {status}. The job is retried with a growing delay.");

    public static SyncError Timeout(string recipe, TimeSpan timeout) => new(
        HttpErrorCodes.Timeout, SyncErrorKind.Unavailable,
        $"Recipe '{recipe}': the API did not answer within {timeout.TotalSeconds:0.#} s. The job is retried; if it keeps " +
        "happening, ask for smaller pages or raise Timeout in AddSyncLaneHttp.");

    public static SyncError Unreachable(string recipe) => new(
        HttpErrorCodes.Unreachable, SyncErrorKind.Unavailable,
        $"Recipe '{recipe}': the API could not be reached. The inner exception says whether DNS, the connection or TLS " +
        "failed. The job is retried.");

    public static SyncError RequestRejected(string recipe, int status) => new(
        HttpErrorCodes.RequestRejected, SyncErrorKind.Configuration,
        $"Recipe '{recipe}': the API refused the request with {status}. Retrying will not change that: compare the " +
        "recipe with the API's documentation, then requeue the job.");

    public static SyncError Redirected(string recipe, int status) => new(
        HttpErrorCodes.Redirected, SyncErrorKind.Configuration,
        $"Recipe '{recipe}': the API answered {status} with a redirect. SyncLane does not follow redirects, so a recipe " +
        "only ever calls the host it names. Put the final URL in the recipe.");

    public static SyncError ResponseTooLarge(string recipe, long limit) => new(
        HttpErrorCodes.ResponseTooLarge, SyncErrorKind.Configuration,
        $"Recipe '{recipe}': the response is larger than {limit:N0} bytes. Ask for smaller pages, or raise " +
        "MaxResponseBytes in AddSyncLaneHttp.");

    public static SyncError ResponseNotJson(string recipe) => new(
        HttpErrorCodes.ResponseNotJson, SyncErrorKind.Unavailable,
        $"Recipe '{recipe}': the response is not valid JSON. It may have been cut off; the job is retried.");

    public static SyncError RecordsNotArray(string recipe, string path, string kind) => new(
        HttpErrorCodes.RecordsNotArray, SyncErrorKind.Configuration,
        $"Recipe '{recipe}': records '{path}' leads to a JSON {kind}, not an array. Point the path at the list of records.");
}
