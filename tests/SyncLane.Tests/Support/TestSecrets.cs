namespace SyncLane.Tests.Support;

/// <summary>
/// Signing secrets for tests, built when the tests run. Secret scanners look for "whsec_" followed by base64 in source
/// files, and a payment provider uses the same prefix, so even a made-up secret written out raises an alert.
/// </summary>
internal static class TestSecrets
{
    /// <summary>A Standard Webhooks secret: <c>whsec_</c> and 32 fixed bytes, the same on every run.</summary>
    public static readonly string Webhook = "whsec_" + Convert.ToBase64String([.. Enumerable.Range(1, 32).Select(index => (byte)index)]);
}
