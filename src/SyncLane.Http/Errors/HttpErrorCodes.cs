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

    /// <summary>The recipe uses the job's window, and the job has none.</summary>
    public const string WindowMissing = "SL5101";

    /// <summary>A rendered header value contains a line break.</summary>
    public const string RenderedValueInvalid = "SL5102";

    /// <summary>The records path leads nowhere in the response.</summary>
    public const string RecordsNotFound = "SL5103";

    /// <summary>The records path leads to something that is not an array.</summary>
    public const string RecordsNotArray = "SL5104";
}
