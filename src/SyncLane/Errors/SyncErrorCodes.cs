namespace SyncLane;

/// <summary>
/// Every error code SyncLane uses. A code is never reused or renumbered. The first digit is the area:
/// 1 configuration, 2 schema, 3 calls to <see cref="ISyncLane"/>, 4 running jobs. Area 5 belongs to the HTTP source
/// and is listed in <c>SyncLane.Http.HttpErrorCodes</c>.
/// </summary>
public static class SyncErrorCodes
{
    /// <summary>No connection string was set.</summary>
    public const string ConnectionStringMissing = "SL1001";

    /// <summary>The schema name is not a plain lower-case identifier.</summary>
    public const string SchemaNameInvalid = "SL1002";

    /// <summary>A global interval is zero or negative.</summary>
    public const string IntervalNotPositive = "SL1003";

    /// <summary>Two queues have the same name.</summary>
    public const string QueueDeclaredTwice = "SL1004";

    /// <summary>Two job types of one queue have the same name.</summary>
    public const string JobTypeDeclaredTwice = "SL1005";

    /// <summary>A queue setting cannot work.</summary>
    public const string QueueSettingInvalid = "SL1006";

    /// <summary>A job type setting cannot work.</summary>
    public const string JobTypeSettingInvalid = "SL1007";

    /// <summary>The database lacks a migration and <c>CreateSchema</c> is off.</summary>
    public const string MigrationMissing = "SL2001";

    /// <summary>The jobs table holds a status this version does not know.</summary>
    public const string UnknownStatus = "SL2002";

    /// <summary>The jobs table holds a mode this version does not know.</summary>
    public const string UnknownMode = "SL2003";

    /// <summary>The call names a queue that was not declared.</summary>
    public const string QueueNotDeclared = "SL3001";

    /// <summary>The call names a job type that is not declared on the queue.</summary>
    public const string JobTypeNotDeclared = "SL3002";

    /// <summary>The page size of a query is out of range.</summary>
    public const string LimitOutOfRange = "SL3003";

    /// <summary>The cursor of a query was not produced by SyncLane.</summary>
    public const string CursorInvalid = "SL3004";

    /// <summary>A job with the same owner, type and dedup key already exists.</summary>
    public const string JobAlreadyExists = "SL3101";

    /// <summary>A job was taken whose type has no handler in this process.</summary>
    public const string HandlerMissing = "SL4001";

    /// <summary>A job was taken more often than its attempts allow without reporting a result.</summary>
    public const string TooManyPickups = "SL4002";
}
