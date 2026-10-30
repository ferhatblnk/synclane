namespace SyncLane.Http;

/// <summary>
/// Every error code of the HTTP source. A code is never reused or renumbered. <c>SL50xx</c> is a recipe that cannot
/// work as written; <c>SL51xx</c> is a recipe that met a job or a response it does not fit.
/// </summary>
public static class HttpErrorCodes
{
    /// <summary>The recipe is not valid JSON, or does not have the shape of a recipe.</summary>
    public const string RecipeJsonInvalid = "SL5001";

    /// <summary>A field the recipe needs is missing or empty.</summary>
    public const string FieldMissing = "SL5002";

    /// <summary>The request URL is not an absolute https URL.</summary>
    public const string UrlInvalid = "SL5003";

    /// <summary>A placeholder names a value that does not exist.</summary>
    public const string PlaceholderUnknown = "SL5004";

    /// <summary>A placeholder appears in the scheme, host or port of the URL.</summary>
    public const string PlaceholderInHost = "SL5005";

    /// <summary>The records path is not in the supported syntax.</summary>
    public const string RecordsPathInvalid = "SL5006";

    /// <summary>The window format is not one SyncLane knows.</summary>
    public const string WindowFormatUnknown = "SL5007";

    /// <summary>The request method is not GET or POST.</summary>
    public const string MethodUnsupported = "SL5008";

    /// <summary>A header name is not a valid token, or a header value contains a line break.</summary>
    public const string HeaderInvalid = "SL5009";

    /// <summary>A body is set on a request whose method sends none.</summary>
    public const string BodyNotAllowed = "SL5010";

    /// <summary>A template has an unclosed or empty placeholder.</summary>
    public const string TemplateMalformed = "SL5011";

    /// <summary>A setting of the HTTP source cannot work.</summary>
    public const string OptionInvalid = "SL5012";

    /// <summary>A recipe was registered without saying where its records go.</summary>
    public const string SinkMissing = "SL5013";

    /// <summary>A job type runs with <c>HttpRecipeHandler</c> and has no recipe.</summary>
    public const string RecipeMissing = "SL5014";

    /// <summary>A recipe was registered for a job type that no queue runs with <c>HttpRecipeHandler</c>.</summary>
    public const string RecipeUnused = "SL5015";

    /// <summary>Two recipes were registered for one job type.</summary>
    public const string RecipeDeclaredTwice = "SL5016";

    /// <summary>The pagination of a recipe cannot work as written.</summary>
    public const string PaginationInvalid = "SL5017";

    /// <summary>The authentication of a recipe cannot work as written.</summary>
    public const string AuthInvalid = "SL5018";

    /// <summary>A recipe sets a header that carries credentials, which never belong in a recipe.</summary>
    public const string CredentialInRecipe = "SL5019";

    /// <summary>A recipe uses authentication and no credential store is registered.</summary>
    public const string CredentialStoreMissing = "SL5020";

    /// <summary>A recipe names a request signer that is not registered.</summary>
    public const string SignerMissing = "SL5021";

    /// <summary>The recipe uses the job's window, and the job has none.</summary>
    public const string WindowMissing = "SL5101";

    /// <summary>A rendered header value contains a line break.</summary>
    public const string RenderedValueInvalid = "SL5102";

    /// <summary>The records path leads nowhere in the response.</summary>
    public const string RecordsNotFound = "SL5103";

    /// <summary>The records path leads to something that is not an array.</summary>
    public const string RecordsNotArray = "SL5104";

    /// <summary>The API answered 429: slow down.</summary>
    public const string Throttled = "SL5105";

    /// <summary>The API answered 401 or 403: the credentials are wrong or no longer allowed.</summary>
    public const string Unauthorized = "SL5106";

    /// <summary>The API answered 408 or a 5xx status.</summary>
    public const string ServerError = "SL5107";

    /// <summary>The API did not answer within the timeout.</summary>
    public const string Timeout = "SL5108";

    /// <summary>The API could not be reached: DNS, connection or TLS failed.</summary>
    public const string Unreachable = "SL5109";

    /// <summary>The API refused the request with a 4xx status that retrying will not change.</summary>
    public const string RequestRejected = "SL5110";

    /// <summary>The API answered with a redirect, which SyncLane does not follow.</summary>
    public const string Redirected = "SL5111";

    /// <summary>The response is larger than the limit.</summary>
    public const string ResponseTooLarge = "SL5112";

    /// <summary>The response is not valid JSON.</summary>
    public const string ResponseNotJson = "SL5113";

    /// <summary>The next page's URL points to another scheme, host or port than the recipe's.</summary>
    public const string NextUrlForeignHost = "SL5114";

    /// <summary>The job's cursor cannot be read by the recipe's pagination.</summary>
    public const string CursorUnreadable = "SL5115";

    /// <summary>The answer holds a cursor or a has-more flag of the wrong JSON type.</summary>
    public const string CursorValueInvalid = "SL5116";

    /// <summary>The answer points to the page that was just read, which would loop forever.</summary>
    public const string PageRepeated = "SL5117";

    /// <summary>A job reached the most pages one job may read.</summary>
    public const string TooManyPages = "SL5118";

    /// <summary>The credential store has no credential for the job's owner.</summary>
    public const string CredentialMissing = "SL5119";

    /// <summary>The owner's credential lacks a value the recipe's auth type needs, or holds one it cannot send.</summary>
    public const string CredentialInvalid = "SL5120";

    /// <summary>The OAuth 2.0 token endpoint refused to issue a token.</summary>
    public const string TokenRefused = "SL5121";

    /// <summary>The OAuth 2.0 token endpoint failed, timed out or could not be reached.</summary>
    public const string TokenUnavailable = "SL5122";

    /// <summary>The OAuth 2.0 token endpoint answered, but not with a usable bearer token.</summary>
    public const string TokenAnswerInvalid = "SL5123";

    /// <summary>A request signer changed the method, scheme, host or port of the request.</summary>
    public const string SignerMovedRequest = "SL5124";
}
