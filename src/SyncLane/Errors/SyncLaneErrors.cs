namespace SyncLane;

/// <summary>
/// The one place where SyncLane words its errors. Every message says what went wrong and how to fix it.
/// Nothing else in the library writes an error message inline; a convention test enforces it.
/// </summary>
internal static class SyncLaneErrors
{
    // 1xxx: configuration, found at start-up.

    public static SyncError ConnectionStringMissing() => new(
        SyncErrorCodes.ConnectionStringMissing, SyncErrorKind.Configuration,
        "SyncLane needs a ConnectionString. Set options.ConnectionString in AddSyncLane, for example from configuration.");

    public static SyncError SchemaNameInvalid(string? schema) => new(
        SyncErrorCodes.SchemaNameInvalid, SyncErrorKind.Configuration,
        $"Schema '{schema}' is not allowed. A schema must start with a lower-case letter or underscore and contain only " +
        "lower-case letters, digits and underscores, at most 63 characters.");

    public static SyncError IntervalNotPositive(string setting) => new(
        SyncErrorCodes.IntervalNotPositive, SyncErrorKind.Configuration,
        $"{setting} must be longer than zero.");

    public static SyncError QueueDeclaredTwice(string queue) => new(
        SyncErrorCodes.QueueDeclaredTwice, SyncErrorKind.Configuration,
        $"Queue '{queue}' is declared twice. Give each AddQueue call its own name.");

    public static SyncError JobTypeDeclaredTwice(string queue, string type) => new(
        SyncErrorCodes.JobTypeDeclaredTwice, SyncErrorKind.Configuration,
        $"Job type '{type}' is declared twice on queue '{queue}'. Give each type of a queue its own name.");

    public static SyncError QueueSettingInvalid(string queue, string setting, string rule) => new(
        SyncErrorCodes.QueueSettingInvalid, SyncErrorKind.Configuration,
        $"Queue '{queue}': {setting} {rule}.");

    public static SyncError JobTypeSettingInvalid(string queue, string type, string rule) => new(
        SyncErrorCodes.JobTypeSettingInvalid, SyncErrorKind.Configuration,
        $"Queue '{queue}', job type '{type}': {rule}.");

    public static SyncError BudgetDeclaredTwice(string budget) => new(
        SyncErrorCodes.BudgetDeclaredTwice, SyncErrorKind.Configuration,
        $"Budget '{budget}' is declared twice. Declare each budget once in AddSyncLane; every queue can draw from it.");

    public static SyncError BudgetSettingInvalid(string budget, string rule) => new(
        SyncErrorCodes.BudgetSettingInvalid, SyncErrorKind.Configuration,
        $"Budget '{budget}': {rule}.");

    // 2xxx: schema and stored data.

    public static SyncError MigrationMissing(string schema, IEnumerable<int> versions) => new(
        SyncErrorCodes.MigrationMissing, SyncErrorKind.Configuration,
        $"The SyncLane schema '{schema}' is missing migration {string.Join(", ", versions)}. CreateSchema is off, so SyncLane " +
        $"does not change the database itself. Run the SQL from SyncLaneSchema.Script(\"{schema}\") in your own migration, " +
        "or set CreateSchema to true to let SyncLane apply it at start-up.");

    public static SyncError UnknownStatus(string text) => new(
        SyncErrorCodes.UnknownStatus, SyncErrorKind.Internal,
        $"The jobs table holds the status '{text}', which this version of SyncLane does not know. The row was written by a " +
        "newer version or by hand. Upgrade every instance to the same version, or correct the row.");

    public static SyncError UnknownMode(string text) => new(
        SyncErrorCodes.UnknownMode, SyncErrorKind.Internal,
        $"The jobs table holds the mode '{text}', which this version of SyncLane does not know. The row was written by a " +
        "newer version or by hand. Upgrade every instance to the same version, or correct the row.");

    // 3xxx: calls to ISyncLane. Returned as failed results, never thrown.

    public static SyncError QueueNotDeclared(string queue) => new(
        SyncErrorCodes.QueueNotDeclared, SyncErrorKind.NotFound,
        $"Queue '{queue}' is not declared. Declare it with AddQueue(\"{queue}\", ...) in AddSyncLane, or check the spelling.");

    public static SyncError JobTypeNotDeclared(string queue, string type) => new(
        SyncErrorCodes.JobTypeNotDeclared, SyncErrorKind.NotFound,
        $"Job type '{type}' is not declared on queue '{queue}'. Declare it with Snapshot, Windowed or Handle on that queue.");

    public static SyncError LimitOutOfRange(int limit) => new(
        SyncErrorCodes.LimitOutOfRange, SyncErrorKind.Validation,
        $"Limit {limit} is out of range. Ask for 1 to {JobQuery.MaxLimit} jobs and follow NextCursor for more.");

    public static SyncError CursorInvalid() => new(
        SyncErrorCodes.CursorInvalid, SyncErrorKind.Validation,
        "The cursor was not produced by SyncLane. Pass the NextCursor of the previous page unchanged, or leave it out to start from the newest job.");

    public static SyncError JobAlreadyExists(string queue, string owner, string type, string? dedupKey) => new(
        SyncErrorCodes.JobAlreadyExists, SyncErrorKind.Conflict,
        $"A '{type}' job of owner '{owner}' with dedup key '{dedupKey}' already exists on queue '{queue}'. Requeue it, or use another key.");

    public static SyncError BudgetNotDeclared(string budget) => new(
        SyncErrorCodes.BudgetNotDeclared, SyncErrorKind.Configuration,
        $"Budget '{budget}' is not declared. Declare it with lane.AddBudget(\"{budget}\", ...) in AddSyncLane, or correct the name.");

    // 4xxx: running jobs. Written to the error column of the job.

    public static SyncError HandlerMissing(string queue, string type) => new(
        SyncErrorCodes.HandlerMissing, SyncErrorKind.Configuration,
        $"No handler is declared for job type '{type}' on queue '{queue}' in this process. Declare the type on every " +
        "instance that runs workers, then requeue the job.");

    public static SyncError TooManyPickups() => new(
        SyncErrorCodes.TooManyPickups, SyncErrorKind.Internal,
        "The job was picked up more often than its attempts allow without ever reporting a result. Its worker probably " +
        "stops while running it: look for crashes or out-of-memory kills during this job, then requeue it.");

    public static SyncError BudgetWaitTooLong(string budget, TimeSpan wait, TimeSpan maxWait) => new(
        SyncErrorCodes.BudgetWaitTooLong, SyncErrorKind.Throttled,
        $"Budget '{budget}' has no token for {wait.TotalSeconds:0.#} s, longer than MaxBudgetWait ({maxWait.TotalSeconds:0.#} s). " +
        "The job gave its slot back without using an attempt and runs again when the token is due. If this happens " +
        "often, the budget is smaller than the work: raise its limit if the API allows, or plan fewer jobs.");
}
