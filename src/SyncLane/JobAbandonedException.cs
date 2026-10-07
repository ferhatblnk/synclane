namespace SyncLane;

/// <summary>Thrown by a handler when retrying cannot help, for example when the account's access was revoked. The job is abandoned at once.</summary>
public sealed class JobAbandonedException(string message, Exception? innerException = null)
    : Exception(message, innerException);
