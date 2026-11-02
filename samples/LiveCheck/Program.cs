using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveCheck;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SyncLane;
using SyncLane.Http;

// The live check: public APIs, one per paging style, synced through the real engine with recipes and no
// code of their own. Run by hand, never in CI: live APIs are slow, rate limited and change. See README.md.

const string Queue = "live";
var patience = TimeSpan.FromMinutes(5);

var connectionString = Environment.GetEnvironmentVariable("SYNCLANE_LIVE_DB");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Set SYNCLANE_LIVE_DB to a PostgreSQL connection string. The check uses a schema of its own and drops it at the end.");
    return 2;
}

var now = TimeProvider.System.GetUtcNow();
var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
var (dayStart, dayEnd) = (today.AddDays(-1), today);
var schema = $"livecheck_{now:yyyyMMddHHmmss}";

LiveCase[] cases =
[
    new("breweries", "page", "breweries", Total: ("https://api.openbrewerydb.org/v1/breweries/meta?by_state=oregon", "total")),
    new("earthquakes", "offset", "earthquakes", Window: (dayStart, dayEnd), Total: (
        $"https://earthquake.usgs.gov/fdsnws/event/1/count?format=geojson&starttime={dayStart:yyyy-MM-dd'T'HH:mm:ss'Z'}&endtime={dayEnd:yyyy-MM-dd'T'HH:mm:ss'Z'}",
        "count")),
    new("author-feed", "cursor", "bsky.app"),
    new("pokemon", "nextUrl", "pokemon", Total: ("https://pokeapi.co/api/v2/pokemon?limit=1", "count")),
    new("issues", "linkHeader", "issues", Window: (dayStart, dayEnd)),

    // Authentication against a real API, when a token is given. Any GitHub token works; it needs no scope.
    .. EnvironmentCredentials.Token is null ? [] : new[] { new LiveCase("issues-auth", "linkHeader", "issues-auth", Window: (dayStart, dayEnd)) }
];

var tallies = cases.ToDictionary(found => found.Type, _ => new Tally());
var userAgent = $"SyncLane-LiveCheck/{typeof(HttpRecipe).Assembly.GetName().Version?.ToString(3)} (+https://github.com/ferhatblnk/synclane)";

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSyncLane(lane =>
{
    lane.ConnectionString = connectionString;
    lane.Schema = schema;
    lane.PollInterval = TimeSpan.FromMilliseconds(250);
    lane.AddQueue(Queue, queue =>
    {
        queue.Concurrency = cases.Length;
        queue.MaxAttempts = 3;
        queue.RetryBaseDelay = TimeSpan.FromSeconds(2);
        queue.ThrottleBaseDelay = TimeSpan.FromSeconds(10);
        foreach (var found in cases)
            queue.Handle<HttpRecipeHandler>(found.Type);
    });
});
builder.Services.AddSyncLaneHttp(http =>
{
    http.UserAgent = userAgent;
    http.Credentials<EnvironmentCredentials>();
    foreach (var found in cases)
    {
        var tally = tallies[found.Type];
        http.Recipe(found.Type, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "recipes", $"{found.Type}.json")))
            .WriteTo((page, _) =>
            {
                tally.Add(page);
                return Task.CompletedTask;
            });
    }
});

using var host = builder.Build();
var clock = Stopwatch.StartNew();

try
{
    await host.StartAsync();
    var client = host.Services.GetRequiredService<ISyncLane>();

    var enqueued = await Task.WhenAll(cases.Select(found => client.EnqueueAsync(new NewJob
    {
        Queue = Queue,
        Owner = new JobOwner(found.Owner),
        Type = found.Type,
        WindowStart = found.Window?.Start,
        WindowEnd = found.Window?.End
    })));
    foreach (var result in enqueued)
        result.ThrowIfFailed();

    Console.WriteLine($"Syncing {cases.Length} APIs into schema {schema}, window {dayStart:yyyy-MM-dd} for the windowed ones...");
    if (EnvironmentCredentials.Token is null)
        Console.WriteLine($"Authentication is not checked: set {EnvironmentCredentials.Variable} to a GitHub token to add it.");
    var jobs = await WaitAsync(client, cases.Length, patience);
    await host.StopAsync();

    using var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    var totals = await Task.WhenAll(cases.Select(found => found.Total is { } total ? CountAsync(http, total.Url, total.Property) : Task.FromResult<long?>(null)));

    var rows = cases.Select((found, index) => Row.Of(found, jobs.FirstOrDefault(job => job.Type == found.Type), tallies[found.Type], totals[index])).ToList();
    Print(rows, clock.Elapsed);
    return rows.All(row => row.Verdict == "ok") ? 0 : 1;
}
finally
{
    await using var data = NpgsqlDataSource.Create(connectionString);
    await using var drop = data.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
    await drop.ExecuteNonQueryAsync();
}

static async Task<IReadOnlyList<Job>> WaitAsync(ISyncLane client, int count, TimeSpan patience)
{
    using var timeout = new CancellationTokenSource(patience);

    // Sequential: polling until every job has ended, or the patience runs out.
    while (true)
    {
        var jobs = (await client.GetJobsAsync(new JobQuery { Queue = Queue }, timeout.Token)).Items;
        if (jobs.Count == count && jobs.All(job => job.Status is JobStatus.Completed or JobStatus.Abandoned))
            return jobs;

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return (await client.GetJobsAsync(new JobQuery { Queue = Queue })).Items;
        }
    }
}

static async Task<long?> CountAsync(HttpClient http, string url, string property)
{
    try
    {
        using var document = JsonDocument.Parse(await http.GetStringAsync(url));
        return document.RootElement.GetProperty(property).GetInt64();
    }
    catch (Exception exception) when (exception is HttpRequestException or JsonException or KeyNotFoundException)
    {
        return null;
    }
}

static void Print(IReadOnlyList<Row> rows, TimeSpan elapsed)
{
    Console.WriteLine();
    Console.WriteLine($"{"Recipe",-34} {"Paging",-10} {"Status",-10} {"Tries",5} {"Pages",6} {"Records",8} {"Distinct",9} {"Expected",9}  Verdict");
    foreach (var row in rows)
        Console.WriteLine($"{row.Recipe,-34} {row.Paging,-10} {row.Status,-10} {row.Attempts,5} {row.Pages,6} {row.Records,8} {row.Distinct,9} {row.Expected?.ToString() ?? "-",9}  {row.Verdict}");

    foreach (var row in rows.Where(row => row.Error is not null))
        Console.WriteLine($"\n{row.Recipe}: {row.Error}");

    Console.WriteLine($"\n{rows.Count(row => row.Verdict == "ok")} of {rows.Count} passed in {elapsed.TotalSeconds:0.0} s.");
}

/// <summary>One public API: its job type (also the recipe's file name), how it pages, and where to read its true total.</summary>
internal sealed record LiveCase(
    string Type,
    string Paging,
    string Owner,
    (DateTimeOffset Start, DateTimeOffset End)? Window = null,
    (string Url, string Property)? Total = null);

/// <summary>What the sink saw: pages, records, and records told apart by their full JSON, so a repeated page shows.</summary>
internal sealed class Tally
{
    private readonly ConcurrentDictionary<string, byte> _distinct = new();
    private int _pages;
    private long _records;

    public int Pages => _pages;

    public long Records => Interlocked.Read(ref _records);

    public int Distinct => _distinct.Count;

    public void Add(RecordPage page)
    {
        Interlocked.Increment(ref _pages);
        Interlocked.Add(ref _records, page.Count);
        foreach (var record in page.Records.EnumerateArray())
            _distinct.TryAdd(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.GetRawText()))), 0);
    }
}

internal sealed record Row(
    string Recipe, string Paging, string Status, int Attempts, int Pages, long Records, int Distinct, long? Expected, string Verdict, string? Error)
{
    public static Row Of(LiveCase found, Job? job, Tally tally, long? expected)
    {
        var verdict = job switch
        {
            null => "not run",
            { Status: not JobStatus.Completed } => "failed",
            _ when tally.Records != tally.Distinct => "repeated records",
            _ when expected is { } total && total != tally.Records => "count differs",
            _ => "ok"
        };

        return new Row(
            found.Type, found.Paging, job?.Status.ToString() ?? "-", job?.AttemptCount ?? 0,
            tally.Pages, tally.Records, tally.Distinct, expected, verdict, job?.Error);
    }
}
