using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using Npgsql;

namespace LoadTest;

/// <summary>
/// Waits until every job of the run has ended, then puts three witnesses side by side: SyncLane's tables, the pages
/// the destination received, and what the pretend API saw. Prints a Markdown report and writes it to /results.
/// </summary>
internal static class Report
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(60);

    public static async Task<int> RunAsync(Settings settings)
    {
        await using var db = NpgsqlDataSource.Create(settings.ConnectionString);
        using var http = new HttpClient { BaseAddress = new Uri(settings.ApiUrl) };
        var began = DateTimeOffset.UtcNow;
        var lastShown = DateTimeOffset.MinValue;

        // Sequential: polling until the run is over.
        while (true)
        {
            var progress = await ProgressAsync(db);
            var now = DateTimeOffset.UtcNow;
            if (now - lastShown >= TimeSpan.FromSeconds(10))
            {
                Console.WriteLine($"{(now - began).TotalSeconds,6:0}s  jobs {progress.Ended}/{progress.Total}  pages {progress.Pages}");
                lastShown = now;
            }

            if (progress.Total > 0 && progress.Ended == progress.Total)
                break;
            if (now - began > GiveUpAfter)
            {
                Console.WriteLine("The run did not end within an hour; the report covers what happened so far.");
                break;
            }

            await Task.Delay(Poll);
        }

        var measured = await MeasureAsync(db, settings);
        var seen = await http.GetFromJsonAsync<ApiStats>("/stats") ?? throw new InvalidOperationException("The pretend API sent no stats.");
        var (markdown, passed) = Format(settings, measured, seen);

        Console.WriteLine();
        Console.WriteLine(markdown);
        Directory.CreateDirectory(settings.ResultsDirectory);
        var file = Path.Combine(settings.ResultsDirectory, $"load-test-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(file, markdown);
        Console.WriteLine($"Written to results/{Path.GetFileName(file)}");
        return passed ? 0 : 1;
    }

    private static async Task<Progress> ProgressAsync(NpgsqlDataSource db)
    {
        try
        {
            await using var command = db.CreateCommand(
                """
                SELECT count(*), count(*) FILTER (WHERE status IN ('completed', 'abandoned')), (SELECT count(*) FROM loadtest.pages)
                FROM synclane.jobs WHERE queue = 'shops'
                """);
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            return new Progress(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new Progress(0, 0, 0);
        }
    }

    private static async Task<Measured> MeasureAsync(NpgsqlDataSource db, Settings settings)
    {
        await using var command = db.CreateCommand(
            """
            WITH jobs AS (
                SELECT * FROM synclane.jobs WHERE queue = 'shops' AND type = 'orders'
            ), written AS (
                SELECT p.*, j.status
                FROM loadtest.pages AS p
                JOIN jobs AS j ON j.owner = p.owner AND j.window_start = p.window_start
            ), per_second AS (
                SELECT count(*) AS pages FROM loadtest.pages GROUP BY date_trunc('second', written_at)
            )
            SELECT
                (SELECT count(*) FROM jobs),
                (SELECT count(*) FROM jobs WHERE status = 'completed'),
                (SELECT count(*) FROM jobs WHERE status = 'abandoned'),
                (SELECT extract(epoch FROM max(completed_at) - min(started_at)) FROM jobs),
                (SELECT count(*) FROM written WHERE status = 'completed'),
                (SELECT coalesce(sum(writes - 1), 0) FROM loadtest.pages),
                (SELECT count(*) FROM loadtest.pages WHERE writes > 1),
                (SELECT count(DISTINCT instance) FROM loadtest.pages),
                (SELECT coalesce(max(pages), 0) FROM per_second),
                current_setting('server_version')
            """);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new Measured(
            Jobs: reader.GetInt64(0),
            Completed: reader.GetInt64(1),
            Abandoned: reader.GetInt64(2),
            Seconds: reader.IsDBNull(3) ? 0 : (double)reader.GetDecimal(3),
            PagesOfCompleted: reader.GetInt64(4),
            ExtraWrites: reader.GetInt64(5),
            PagesWrittenTwice: reader.GetInt64(6),
            Instances: reader.GetInt64(7),
            BusiestSecond: reader.GetInt64(8),
            PostgresVersion: reader.GetString(9),
            ExpectedPages: reader.GetInt64(1) * settings.Pages);
    }

    private static (string Markdown, bool Passed) Format(Settings settings, Measured m, ApiStats api)
    {
        var c = CultureInfo.InvariantCulture;
        var pages = m.PagesOfCompleted;
        var perSecond = m.Seconds > 0 ? pages / m.Seconds : 0;
        var slots = settings.Workers * settings.Slots;
        var perSlot = Math.Min(settings.BudgetLimit, 1000.0 / settings.LatencyMs);
        var ideal = slots * perSlot;
        var allowed = settings.BudgetLimit + 1;

        var checks = new List<(string Rule, string Seen, bool Ok)>
        {
            ("Every job ended, none abandoned", $"{m.Completed:N0} completed, {m.Abandoned} abandoned of {m.Jobs:N0}", m.Completed == m.Jobs && m.Abandoned == 0),
            ("No page lost", $"{m.ExpectedPages - pages:N0} missing of {m.ExpectedPages:N0}", m.ExpectedPages == pages),
            ("No page written twice", $"{m.PagesWrittenTwice:N0} pages, {m.ExtraWrites:N0} extra writes", m.ExtraWrites == 0),
            ("Never two requests of one account at once", $"{api.Overlaps:N0} overlaps", api.Overlaps == 0),
            ("No account above its budget in any second", $"busiest second: {api.BusiestSecond} requests, budget {settings.BudgetLimit}/s + burst 1", api.BusiestSecond <= allowed),
        };
        var passed = checks.All(check => check.Ok);

        var md = new StringBuilder();
        md.AppendLine(c, $"## Load test, {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC");
        md.AppendLine();
        md.AppendLine(c, $"**{(passed ? "Passed" : "FAILED")}.** {settings.Owners:N0} accounts, {m.Jobs:N0} jobs, {m.ExpectedPages:N0} pages, {settings.Workers} instances × {settings.Slots} slots, PostgreSQL {m.PostgresVersion}, {Environment.ProcessorCount} CPUs in the container.");
        md.AppendLine();
        md.AppendLine("| Correctness | Seen | |");
        md.AppendLine("| --- | --- | --- |");
        foreach (var (rule, seen, ok) in checks)
            md.AppendLine(c, $"| {rule} | {seen} | {(ok ? "pass" : "**fail**")} |");
        md.AppendLine();
        md.AppendLine("| Speed | |");
        md.AppendLine("| --- | --- |");
        md.AppendLine(c, $"| Drained in | {m.Seconds:N1} s, first job taken to last completed |");
        md.AppendLine(c, $"| Pages a second | {perSecond:N0} on average, {m.BusiestSecond:N0} in the busiest second |");
        md.AppendLine(c, $"| Records a second | {perSecond * settings.Records:N0} |");
        md.AppendLine(c, $"| Against the ceiling | {perSecond / ideal:P0} of {ideal:N0} pages a second: {slots} slots, each held to {perSlot:N1} pages a second by the budget and the API's {settings.LatencyMs} ms |");
        md.AppendLine(c, $"| Instances that wrote | {m.Instances} of {settings.Workers} |");
        md.AppendLine();
        md.AppendLine("| What the API saw | |");
        md.AppendLine("| --- | --- |");
        md.AppendLine(c, $"| Requests | {api.Requests:N0}: {api.Served:N0} answered, {api.Throttled:N0} refused with 429, {api.Failed:N0} failed with 503 |");
        md.AppendLine(c, $"| Busiest second of one account | {api.BusiestSecondNormal} on an account whose real limit is {settings.HiddenLimit}/s; {api.BusiestSecondHot} on a hot account whose real limit is {settings.HotLimit}/s |");
        md.AppendLine();
        md.AppendLine(c, $"Settings: latency {settings.LatencyMs} ms, failures {settings.FailureRate:P0}, budget {settings.BudgetLimit}/s, the API's burst {settings.HiddenBurst}, hot accounts {settings.HotShare:P0}, history {settings.HorizonDays} days in {settings.SliceDays}-day slices, {settings.Pages} pages of {settings.Records} records per job.");
        return (md.ToString(), passed);
    }

    private readonly record struct Progress(long Total, long Ended, long Pages);

    private sealed record Measured(
        long Jobs, long Completed, long Abandoned, double Seconds, long PagesOfCompleted, long ExtraWrites,
        long PagesWrittenTwice, long Instances, long BusiestSecond, string PostgresVersion, long ExpectedPages);
}
