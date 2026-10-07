namespace SyncLane.Running;

/// <summary>Turns an exception into the text stored in the <c>error</c> column: every exception in the chain, outermost first.</summary>
internal static class ErrorText
{
    public const int MaxLength = 2000;

    private const string Separator = " -> ";

    public static string Describe(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
            parts.Add($"{current.GetType().Name}: {current.Message}");

        var text = string.Join(Separator, parts);
        return text.Length <= MaxLength ? text : text[..MaxLength];
    }
}
