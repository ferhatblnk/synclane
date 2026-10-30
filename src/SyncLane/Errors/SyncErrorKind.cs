namespace SyncLane;

/// <summary>What kind of problem a <see cref="SyncError"/> is. Callers branch on the kind; people read the message.</summary>
public enum SyncErrorKind
{
    /// <summary>The input breaks a rule, such as a limit out of range. Fix the call.</summary>
    Validation,

    /// <summary>Something the call names does not exist, such as an undeclared queue.</summary>
    NotFound,

    /// <summary>The call would create something that already exists.</summary>
    Conflict,

    /// <summary>The setup is wrong: options, schema or registration. Fix the configuration and restart.</summary>
    Configuration,

    /// <summary>The other side refused the credentials or the access.</summary>
    Unauthorized,

    /// <summary>The other side asked to slow down.</summary>
    Throttled,

    /// <summary>A dependency could not be reached. Trying again later may help.</summary>
    Unavailable,

    /// <summary>SyncLane found a state it should never be in. Please report it.</summary>
    Internal
}

/// <summary>Maps an error kind to the HTTP status code an API built on SyncLane would usually answer with.</summary>
public static class SyncErrorKindExtensions
{
    /// <summary>The usual HTTP status code for <paramref name="kind"/>: 400, 404, 409, 401, 429, 503 or 500.</summary>
    public static int ToHttpStatusCode(this SyncErrorKind kind) => kind switch
    {
        SyncErrorKind.Validation => 400,
        SyncErrorKind.NotFound => 404,
        SyncErrorKind.Conflict => 409,
        SyncErrorKind.Unauthorized => 401,
        SyncErrorKind.Throttled => 429,
        SyncErrorKind.Unavailable => 503,
        _ => 500
    };
}
