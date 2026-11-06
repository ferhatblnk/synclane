using System.Security.Cryptography;
using System.Text;

namespace SyncLane.Storage;

/// <summary>
/// The name of the notification channel a schema's job changes are sent on. A channel name must be shorter than 64 bytes,
/// and a schema name may be 63 characters, so a long schema is shortened with a hash that keeps it unique. Migration 8
/// writes the name into its trigger, so this function must never change.
/// </summary>
internal static class ChangeChannel
{
    private const string Prefix = "synclane_";

    private const int MaxLength = 63;

    /// <summary>Sends a probe to the channel a new listening connection listens on first.</summary>
    public const string Probe = "SELECT pg_notify(@channel, '')";

    /// <summary>
    /// A channel of one probe's own. Nothing else ever sends to it, so a registration a pooler keeps on a server
    /// connection after the probe is never heard from again.
    /// </summary>
    public static string ForProbe(Guid probe) => $"{Prefix}probe_{probe:N}";

    public static string For(string schema)
    {
        var name = Prefix + schema;
        if (name.Length <= MaxLength)
            return name;

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(schema)))[..8];
        return $"{Prefix}{schema[..(MaxLength - Prefix.Length - hash.Length - 1)]}_{hash}";
    }
}
