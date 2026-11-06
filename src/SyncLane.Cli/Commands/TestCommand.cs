using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using SyncLane.Http;

namespace SyncLane.Cli.Commands;

/// <summary>
/// <c>synclane test recipe.jsonc</c>: runs the recipe for two pages through the same handler a job uses, with no
/// database, and reports what a person needs to trust it: the records' fields, whether the key is unique, and whether
/// page 2 brought new records. It prints structure, never values: no record, cursor, URL or credential.
/// </summary>
internal static partial class TestCommand
{
    private const string JobType = "recipe";
    private const int Pages = 2;
    private const int ShownFields = 10;

    public static Command Create(CliHost host)
    {
        var recipe = new Argument<string>("recipe") { Description = "The recipe file, such as one 'synclane init' wrote." };
        var owner = new Option<string?>("--owner") { Description = "The owner key to test with, for a recipe that uses {{owner.key}}." };
        var values = new Option<string[]>("--value")
        {
            Description = "A value of the owner, as name=value, for a recipe that uses {{owner.name}}. Repeat it for each.",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = false
        };
        var since = new Option<string>("--since")
        {
            Description = "How far back the window reaches: a length such as 7d, 12h or 30m, or a date.",
            DefaultValueFactory = _ => "7d"
        };

        var command = new Command("test", "Runs a recipe for two pages through the real handler, without a database, and says whether its records, key and paging work.")
        {
            recipe, owner, since, values
        };
        command.SetAction((result, cancellationToken) =>
            RunAsync(result.GetValue(recipe)!, result.GetValue(owner), result.GetValue(since)!, host, cancellationToken, Pairs(result.GetValue(values))));
        return command;
    }

    internal static async Task<int> RunAsync(
        string file, string? owner, string since, CliHost host, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? values = null)
    {
        var path = Path.GetFullPath(file, host.Directory);
        if (!File.Exists(path))
            return host.Fail(CliErrors.RecipeFileMissing(file));

        var parsed = HttpRecipe.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        if (!parsed.Succeeded)
            return host.Fail(parsed.Errors);

        var recipe = parsed.Value!;
        if (recipe.Auth is { Type: AuthType.Signed })
            return host.Fail(CliErrors.SignedNotTestable(recipe.Name));

        // The placeholders say what the recipe needs; a window section is left out when it has the default format.
        var text = JsonSerializer.Serialize(recipe);
        var windowed = recipe.Window is not null || text.Contains("{{window.", StringComparison.Ordinal);
        if (owner is null && text.Contains("{{owner.key}}", StringComparison.Ordinal))
            return host.Fail(CliErrors.OwnerNeeded(recipe.Name));

        var needed = OwnerValue().Matches(text).Select(match => match.Groups["name"].Value).Where(name => name != "key").Distinct(StringComparer.Ordinal).ToList();
        var missing = needed.Where(name => values?.ContainsKey(name) != true).ToList();
        if (missing.Count > 0)
            return host.Fail(CliErrors.OwnerValuesNeeded(recipe.Name, missing));

        var now = host.Time.GetUtcNow();
        if (Start(since, now) is not { } start)
            return host.Fail(CliErrors.SinceInvalid(since));

        TestCredentials.Given? credentials = null;
        if (recipe.Auth is { } auth)
        {
            var gathered = TestCredentials.Gather(auth, host);
            if (!gathered.Succeeded)
                return host.Fail(gathered.Errors);

            credentials = gathered.Value;
        }

        var seen = new List<PageSeen>();
        ServiceProvider provider;
        try
        {
            provider = Services(recipe, credentials, host, seen);
        }
        catch (SyncLaneException exception)
        {
            return host.Fail(exception.Errors);
        }

        await using (provider)
        {
            var job = new Job
            {
                Id = 1,
                Queue = "synclane-test",
                Owner = owner ?? "test",
                Lane = owner ?? "test",
                Type = JobType,
                Mode = JobMode.Manual,
                Status = JobStatus.Running,
                OwnerValues = values,
                WindowStart = windowed ? start : null,
                WindowEnd = windowed ? now : null,
                CreatedAt = now,
                NextRunAt = now
            };

            host.Out.WriteLine($"Testing {recipe.Name}{(windowed ? $", from {start:yyyy-MM-dd HH:mm} to {now:yyyy-MM-dd HH:mm} UTC" : "")}{(owner is null ? "" : $", owner {owner}")}.");
            host.Out.WriteLine();

            var ran = await RunPagesAsync(provider, job, host, cancellationToken);
            if (ran is null)
                return 1;

            return Report(recipe, seen, ran.Value, credentials, host);
        }
    }

    /// <summary>Runs the pages and prints a line for each; <c>null</c> when one failed, whether more pages follow otherwise.</summary>
    private static async Task<bool?> RunPagesAsync(ServiceProvider provider, Job job, CliHost host, CancellationToken cancellationToken)
    {
        await using var scope = provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<HttpRecipeHandler>();
        string? cursor = null;

        // Sequential: each page starts where the page before it ended.
        for (var number = 1; number <= Pages; number++)
        {
            var started = host.Time.GetTimestamp();
            JobPage page;
            try
            {
                page = await handler.RunPageAsync(job with { Cursor = cursor }, cancellationToken);
            }
            catch (Exception exception) when (exception is JobAbandonedException or JobThrottledException or JobPostponedException)
            {
                host.Out.WriteLine($"Page {number} failed.");
                host.Error.WriteLine(exception.Message);
                return null;
            }
            catch (SyncLaneException exception)
            {
                host.Out.WriteLine($"Page {number} failed.");
                host.Fail(exception.Errors);
                return null;
            }

            cursor = page.NextCursor;
            var elapsed = host.Time.GetElapsedTime(started);
            host.Out.WriteLine($"Page {number}  {page.Processed,6} records  {elapsed.TotalMilliseconds,6:0} ms");
            if (cursor is null)
                return false;
        }

        return true;
    }

    private static int Report(HttpRecipe recipe, List<PageSeen> seen, bool more, TestCredentials.Given? credentials, CliHost host)
    {
        var console = host.Out;
        var total = seen.Sum(page => page.Count);
        console.WriteLine();

        if (seen.FirstOrDefault(page => page.Fields.Count > 0) is { } first)
        {
            var shown = string.Join(", ", first.Fields.Take(ShownFields));
            console.WriteLine($"Fields:  {shown}{(first.Fields.Count > ShownFields ? $", and {first.Fields.Count - ShownFields} more" : "")}");
        }

        if (total == 0)
            console.WriteLine("Records: none in the window. Widen it with --since, or check the recipe's records path.");

        var ids = seen.All(page => page.IdError is null);
        var repeated = seen.Count >= 2 && seen[1].Count > 0
            && (ids ? seen[1].Ids : seen[1].Content).All((ids ? seen[0].Ids : seen[0].Content).ToHashSet(StringComparer.Ordinal).Contains);

        if (total > 0)
        {
            if (!ids)
            {
                console.WriteLine($"Key:     {seen.First(page => page.IdError is not null).IdError!.Message}");
            }
            else if (repeated)
            {
                console.WriteLine("Key:     $.id, unique within each page.");
            }
            else
            {
                var all = seen.SelectMany(page => page.Ids).ToList();
                var twice = all.Count - all.Distinct(StringComparer.Ordinal).Count();
                console.WriteLine(twice == 0
                    ? $"Key:     $.id, unique across {all.Count} records."
                    : $"Key:     $.id. {twice} records came on both pages, as happens when records change during paging; destinations keep one copy.");
            }
        }

        if (repeated)
        {
            console.WriteLine("Paging:  page 2 repeated page 1.");
            host.Fail(CliErrors.PageRepeated(2, Paging(recipe.Pagination)));
        }
        else if (seen.Count >= 2 && seen[1].Count > 0)
        {
            console.WriteLine($"Paging:  page 2 brought new records{(more ? "; more pages follow" : "")}.");
        }
        else if (recipe.Pagination is null)
        {
            console.WriteLine("Paging:  none; the recipe reads one page.");
        }
        else if (total > 0)
        {
            console.WriteLine("Paging:  one page held every record in the window, so page 2 was not tried. Widen the window with --since to try it.");
        }

        if (recipe.Rate is { } rate)
            console.WriteLine($"Budget:  '{rate.Budget}' was not used: budgets live in the database. Declare it in AddSyncLane.");

        if (credentials?.Rotated == true)
        {
            console.WriteLine("Warning: the token endpoint issued a new refresh token, and may have retired the one you gave. The new one is not shown; " +
                "if that connection is used elsewhere, it may need connecting again. Test with a test account's token.");
        }

        if (!repeated)
        {
            console.WriteLine();
            console.WriteLine("The recipe works.");
        }

        return repeated ? 1 : 0;
    }

    private static ServiceProvider Services(HttpRecipe recipe, TestCredentials.Given? credentials, CliHost host, List<PageSeen> seen)
    {
        var services = new ServiceCollection();
        services.AddSingleton(host.Time);
        if (credentials is not null)
            services.AddSingleton(credentials);

        services.AddSyncLaneHttp(options =>
        {
            if (credentials is not null)
                options.Credentials<TestCredentials>();

            // A budget lives in the database, and two pages need none.
            options.Recipe(JobType, recipe with { Rate = null }).WriteTo((page, _) =>
            {
                seen.Add(PageSeen.From(page));
                return Task.CompletedTask;
            });
        });

        if (host.Network is { } network)
            services.ConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = network()));

        return services.BuildServiceProvider();
    }

    /// <summary>When the window starts: <paramref name="since"/> before now (<c>7d</c>, <c>12h</c>, <c>30m</c>), or a date.</summary>
    internal static DateTimeOffset? Start(string since, DateTimeOffset now)
    {
        if (since.Length >= 2 && int.TryParse(since.AsSpan(0, since.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var amount) && amount > 0)
        {
            TimeSpan? length = since[^1] switch
            {
                'd' => TimeSpan.FromDays(amount),
                'h' => TimeSpan.FromHours(amount),
                'm' => TimeSpan.FromMinutes(amount),
                _ => null
            };

            if (length is { } span)
                return now - span;
        }

        return DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) && date < now
            ? date
            : null;
    }

    private static string Paging(HttpRecipePagination? pagination) => pagination?.Type switch
    {
        PaginationType.Page => $"page parameter '{pagination.Param ?? "page"}'",
        PaginationType.Offset => $"offset parameter '{pagination.Param ?? "offset"}'",
        PaginationType.Cursor when pagination.Param is { } param => $"cursor parameter '{param}'",
        PaginationType.Cursor => "cursor parameter",
        _ => "next-page address in its own answer"
    };

    /// <summary>What one page showed: its size, its ids or why it has none, a key per record's content, and the first record's fields.</summary>
    private sealed record PageSeen(int Count, IReadOnlyList<string> Ids, SyncError? IdError, IReadOnlyList<string> Content, IReadOnlyList<string> Fields)
    {
        public static PageSeen From(RecordPage page)
        {
            var ids = RecordIdentity.Inferred.Identify(page);
            var content = RecordIdentity.ByContent.Identify(page);
            var first = page.Count > 0 ? page.Records[0] : default;

            return new PageSeen(
                page.Count,
                ids.Succeeded ? [.. ids.Value!.Select(record => record.Key)] : [],
                ids.Succeeded ? null : ids.Errors[0],
                content.Succeeded ? [.. content.Value!.Select(record => record.Key)] : [],
                first.ValueKind == JsonValueKind.Object ? [.. first.EnumerateObject().Select(property => property.Name)] : []);
        }
    }

    /// <summary>name=value pairs as a dictionary; a pair without '=' is a value named by the whole text, which no recipe uses.</summary>
    internal static IReadOnlyDictionary<string, string>? Pairs(string[]? pairs)
    {
        if (pairs is not { Length: > 0 })
            return null;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            values[equals < 0 ? pair : pair[..equals]] = equals < 0 ? "" : pair[(equals + 1)..];
        }

        return values;
    }

    [GeneratedRegex(@"\{\{owner\.(?<name>[A-Za-z][A-Za-z0-9_]{0,63})\}\}")]
    private static partial Regex OwnerValue();
}
