using System.Text.Json;
using System.Text.RegularExpressions;

namespace SyncLane.Planning;

/// <summary>
/// An owner's values (<see cref="JobOwner.Values"/>): checked where they come in, kept as JSON in the owners table, and
/// read back with every job. A name is what a recipe writes after <c>owner.</c>, so <c>key</c> is taken.
/// </summary>
internal static partial class OwnerValues
{
    public const int MaxCount = 20;

    public const int MaxLength = 1000;

    /// <summary>The problem with an owner's values, or <c>null</c> when they can be used. Never names a value, only its name.</summary>
    public static SyncError? Check(JobOwner owner)
    {
        if (owner.Values is not { } values)
            return null;

        if (values.Count > MaxCount)
            return SyncLaneErrors.OwnerValueInvalid(owner.Key, "(all)", $"an owner has at most {MaxCount} values");

        foreach (var (name, value) in values)
        {
            if (!Name().IsMatch(name) || name == "key")
                return SyncLaneErrors.OwnerValueInvalid(owner.Key, name, "a name is 1 to 64 letters, digits or underscores, starting with a letter, and not 'key'");

            if (value is null || value.Length > MaxLength || value.AsSpan().ContainsAnyInRange('\0', '\u001f') || value.Contains('\u007f'))
                return SyncLaneErrors.OwnerValueInvalid(owner.Key, name, $"a value is up to {MaxLength} characters on one line, without control characters");
        }

        return null;
    }

    public static string? ToJson(IReadOnlyDictionary<string, string>? values) =>
        values is null ? null : JsonSerializer.Serialize(values);

    public static IReadOnlyDictionary<string, string>? FromJson(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(json);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex Name();
}
