using System.Globalization;

namespace LoadTest;

/// <summary>
/// Everything a run can change, read from the environment so that one image serves the API, the workers and the
/// report. The defaults are the published scenario.
/// </summary>
internal sealed record Settings
{
    /// <summary>Accounts connected at once; each is an owner with a lane of its own.</summary>
    public int Owners { get; init; } = Int("OWNERS", 1000);

    /// <summary>Pages in every job, and records in every page.</summary>
    public int Pages { get; init; } = Int("PAGES", 10);

    public int Records { get; init; } = Int("RECORDS", 100);

    /// <summary>How long the pretend API takes to answer a page.</summary>
    public int LatencyMs { get; init; } = Int("LATENCY_MS", 50);

    /// <summary>The share of answers that fail with a 503.</summary>
    public double FailureRate { get; init; } = Double("FAILURE_RATE", 0.01);

    /// <summary>The API's real limit per account and second, which SyncLane is not told.</summary>
    public int HiddenLimit { get; init; } = Int("HIDDEN_LIMIT", 30);

    /// <summary>The share of accounts whose real limit is lower than the budget, so they answer 429.</summary>
    public double HotShare { get; init; } = Double("HOT_SHARE", 0.05);

    public int HotLimit { get; init; } = Int("HOT_LIMIT", 8);

    /// <summary>How many requests the API takes at once before its limit applies: small, as most APIs keep it.</summary>
    public int HiddenBurst { get; init; } = Int("HIDDEN_BURST", 2);

    /// <summary>The budget declared to SyncLane, per account and second.</summary>
    public int BudgetLimit { get; init; } = Int("BUDGET_LIMIT", 15);

    /// <summary>Worker slots in each instance, and the number of instances the report divides by.</summary>
    public int Slots { get; init; } = Int("SLOTS", 16);

    public int Workers { get; init; } = Int("WORKERS", 4);

    public int HorizonDays { get; init; } = Int("HORIZON_DAYS", 730);

    public int SliceDays { get; init; } = Int("SLICE_DAYS", 30);

    public string ApiUrl { get; init; } = Text("API_URL", "http://api:8080");

    /// <summary>Where the pretend API listens: every interface inside its container.</summary>
    public string ApiListen { get; init; } = Text("API_LISTEN", "http://0.0.0.0:8080");

    /// <summary>Where the report is written.</summary>
    public string ResultsDirectory { get; init; } = Text("RESULTS_DIR", "/results");

    public string ConnectionString { get; init; } = Text("POSTGRES", "Host=postgres;Username=postgres;Database=loadtest;Maximum Pool Size=60");

    /// <summary>Accounts are named <c>acct-0001</c> and on; every twentieth one, by default, is hot.</summary>
    public bool IsHot(string account) =>
        HotShare > 0 && int.TryParse(account.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var n) &&
        n % (int)Math.Round(1 / HotShare) == 0;

    public static string Account(int n) => $"acct-{n:D4}";

    private static int Int(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static double Double(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static string Text(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
}
