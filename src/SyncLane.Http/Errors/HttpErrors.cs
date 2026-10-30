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

    public static SyncError PaginationInvalid(string recipe, string field, string rule) => new(
        HttpErrorCodes.PaginationInvalid, SyncErrorKind.Validation,
        $"Recipe '{recipe}': pagination.{field} {rule}. See the pagination types in docs/recipes.md.");

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

    public static SyncError NextUrlForeignHost(string recipe) => new(
        HttpErrorCodes.NextUrlForeignHost, SyncErrorKind.Configuration,
        $"Recipe '{recipe}': the next page's URL points to another scheme, host or port than the recipe's URL. It was " +
        "not called, so the recipe's headers never reach another host. If the API really moves between hosts, write a " +
        "recipe per host.");

    public static SyncError CursorUnreadable(string recipe, string type) => new(
        HttpErrorCodes.CursorUnreadable, SyncErrorKind.Configuration,
        $"Recipe '{recipe}': the job's cursor is not a valid {type} cursor. The recipe's pagination may have changed " +
        "while the job was halfway. Requeue the job after clearing its cursor, so it starts from the first page.");

    public static SyncError CursorValueInvalid(string recipe, string field, string expected) => new(
        HttpErrorCodes.CursorValueInvalid, SyncErrorKind.Configuration,
        $"Recipe '{recipe}': pagination.{field} leads to a value that is not {expected}. Compare the path with a real response.");

    public static SyncError PageRepeated(string recipe) => new(
        HttpErrorCodes.PageRepeated, SyncErrorKind.Configuration,
        $"Recipe '{recipe}': the answer points to the page that was just read. Following it would loop forever, so the " +
        "job stopped. Check that the recipe sends the cursor in the parameter the API reads.");

    public static SyncError TooManyPages(string recipe, int limit) => new(
        HttpErrorCodes.TooManyPages, SyncErrorKind.Configuration,
        $"Recipe '{recipe}': the job reached {limit:N0} pages. Either the API ignores the page parameter and returns the " +
        "same page again, or a window holds more than expected: check the parameter, cut smaller slices, or raise " +
        "MaxPagesPerJob in AddSyncLaneHttp.");

    public static SyncError RecordsNotArray(string recipe, string path, string kind) => new(
        HttpErrorCodes.RecordsNotArray, SyncErrorKind.Configuration,
        $"Recipe '{recipe}': records '{path}' leads to a JSON {kind}, not an array. Point the path at the list of records.");
}
