namespace SyncLane;

/// <summary>
/// One problem, with a stable code. The code never changes once released, so it is safe to branch on, to alert on and to
/// use as a localization key. Every code is listed with its cause and fix in <c>docs/errors.md</c>.
/// </summary>
/// <param name="Code">The stable code, such as <c>SL3001</c>. See <see cref="SyncErrorCodes"/>.</param>
/// <param name="Kind">What kind of problem it is.</param>
/// <param name="Message">What went wrong and how to fix it, in English.</param>
public sealed record SyncError(string Code, SyncErrorKind Kind, string Message)
{
    /// <summary>The code and the message, the form used in exceptions, logs and the <c>error</c> column.</summary>
    public override string ToString() => $"{Code}: {Message}";
}
