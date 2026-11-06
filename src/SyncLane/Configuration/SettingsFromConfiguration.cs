using System.Globalization;
using Microsoft.Extensions.Configuration;
using SyncLane.Planning;

namespace SyncLane;

/// <summary>
/// Applies a configuration section over what the code declared, so numbers can differ per environment without a
/// deploy. It reads <see cref="IConfiguration"/> by hand, without the binder: names ignore case, because configuration
/// keys do (an environment variable is often written in capitals), and values are parsed with the invariant culture, as
/// the binder does. A name it does not know is an error instead of being skipped, so a typo never passes silently.
/// Every problem is reported at once.
/// </summary>
internal static class SettingsFromConfiguration
{
    private const string WholeNumber = "a whole number";
    private const string Span = "a time span such as 00:15:00 or 7.00:00:00";
    private const string Flag = "true or false";
    private const string Number = "a number such as 0.25";
    private const string Text = "text";
    private const string Entries = "a section with one entry per name";

    private delegate bool Parse<T>(string text, out T value);

    public static void Apply(SyncLaneOptions options, IConfiguration configuration)
    {
        var errors = new List<SyncError>();

        Walk(configuration, errors, new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(SyncLaneOptions.ConnectionString)] = section => Read<string>(section, Text, Words, value => options.ConnectionString = value, errors),
            [nameof(SyncLaneOptions.ListenConnectionString)] = section => Read<string>(section, Text, Words, value => options.ListenConnectionString = value, errors),
            [nameof(SyncLaneOptions.Schema)] = section => Read<string>(section, Text, Words, value => options.Schema = value, errors),
            [nameof(SyncLaneOptions.CreateSchema)] = section => Read<bool>(section, Flag, bool.TryParse, value => options.CreateSchema = value, errors),
            [nameof(SyncLaneOptions.RunPlanner)] = section => Read<bool>(section, Flag, bool.TryParse, value => options.RunPlanner = value, errors),
            [nameof(SyncLaneOptions.RunWorkers)] = section => Read<bool>(section, Flag, bool.TryParse, value => options.RunWorkers = value, errors),
            [nameof(SyncLaneOptions.PlanInterval)] = section => Read<TimeSpan>(section, Span, Spans, value => options.PlanInterval = value, errors),
            [nameof(SyncLaneOptions.PollInterval)] = section => Read<TimeSpan>(section, Span, Spans, value => options.PollInterval = value, errors),
            [nameof(SyncLaneOptions.MaxBudgetWait)] = section => Read<TimeSpan>(section, Span, Spans, value => options.MaxBudgetWait = value, errors),
            [nameof(SyncLaneOptions.Application)] = section => Read<string>(section, Text, Words, value => options.Application = value, errors),
            [nameof(SyncLaneOptions.KeepCompletedJobs)] = section => Read<TimeSpan>(section, Span, Spans, value => options.KeepCompletedJobs = value, errors),
            ["Queues"] = section => Each(section, errors, options.Queues.Select(queue => queue.Name), name => options.Find(name), (queue, entry) => Queue(queue, entry, errors)),
            ["Budgets"] = section => Each(section, errors, options.DescribeBudgets().Select(budget => budget.Name), name => options.FindBudget(name), (budget, entry) => Budget(options, budget, entry, errors))
        });

        if (errors.Count > 0)
            throw new SyncLaneException(errors);
    }

    private static void Queue(QueueOptions queue, IConfigurationSection section, List<SyncError> errors) =>
        Walk(section, errors, new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(QueueOptions.Concurrency)] = entry => Read<int>(entry, WholeNumber, Whole, value => queue.Concurrency = value, errors),
            [nameof(QueueOptions.MaxAttempts)] = entry => Read<int>(entry, WholeNumber, Whole, value => queue.MaxAttempts = value, errors),
            [nameof(QueueOptions.LeaseDuration)] = entry => Read<TimeSpan>(entry, Span, Spans, value => queue.LeaseDuration = value, errors),
            [nameof(QueueOptions.RetryBaseDelay)] = entry => Read<TimeSpan>(entry, Span, Spans, value => queue.RetryBaseDelay = value, errors),
            [nameof(QueueOptions.RetryMaxDelay)] = entry => Read<TimeSpan>(entry, Span, Spans, value => queue.RetryMaxDelay = value, errors),
            [nameof(QueueOptions.ThrottleBaseDelay)] = entry => Read<TimeSpan>(entry, Span, Spans, value => queue.ThrottleBaseDelay = value, errors),
            [nameof(QueueOptions.ThrottleMaxDelay)] = entry => Read<TimeSpan>(entry, Span, Spans, value => queue.ThrottleMaxDelay = value, errors),
            ["Types"] = entry => Each(entry, errors, queue.Types.Select(type => type.Name), queue.Find, (type, settings) => JobType(queue, type, settings, errors))
        });

    private static void JobType(QueueOptions queue, JobType type, IConfigurationSection section, List<SyncError> errors)
    {
        var rule = type.Rule;
        var settings = new Dictionary<string, Action<IConfigurationSection>>(StringComparer.OrdinalIgnoreCase);

        if (rule is not null)
        {
            settings["Every"] = entry => Read<TimeSpan>(entry, Span, Spans, value => rule = rule! with { Every = value }, errors);
            settings["Offset"] = entry => Read<TimeSpan>(entry, Span, Spans, value => rule = rule! with { Offset = value }, errors);
        }

        if (rule is WindowedRule)
        {
            settings["Horizon"] = entry => Read<TimeSpan>(entry, Span, Spans, value => rule = (WindowedRule)rule! with { Horizon = value }, errors);
            settings["Slice"] = entry => Read<TimeSpan>(entry, Span, Spans, value => rule = (WindowedRule)rule! with { Slice = value }, errors);
            settings["Overlap"] = entry => Read<TimeSpan>(entry, Span, Spans, value => rule = (WindowedRule)rule! with { Overlap = value }, errors);
        }

        Walk(section, errors, settings);
        if (rule is not null && rule != type.Rule)
            queue.Override(type.Name, rule);
    }

    private static void Budget(SyncLaneOptions options, BudgetDescription budget, IConfigurationSection section, List<SyncError> errors)
    {
        var changed = budget;
        Walk(section, errors, new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(BudgetDescription.Limit)] = entry => Read<int>(entry, WholeNumber, Whole, value => changed = changed with { Limit = value }, errors),
            [nameof(BudgetDescription.Per)] = entry => Read<TimeSpan>(entry, Span, Spans, value => changed = changed with { Per = value }, errors),
            [nameof(BudgetDescription.Burst)] = entry => Read<int>(entry, WholeNumber, Whole, value => changed = changed with { Burst = value }, errors),
            [nameof(BudgetDescription.Start)] = entry => Read<double>(entry, Number, Fraction, value => changed = changed with { Start = value }, errors)
        });

        if (changed != budget)
            options.Override(changed);
    }

    /// <summary>Applies each child of <paramref name="section"/> by its setting name; one it does not know is an error.</summary>
    private static void Walk(IConfiguration section, List<SyncError> errors, Dictionary<string, Action<IConfigurationSection>> settings)
    {
        foreach (var child in section.GetChildren())
        {
            if (settings.TryGetValue(child.Key, out var apply))
                apply(child);
            else
                errors.Add(SyncLaneErrors.SettingNotDeclared(child.Path, settings.Keys));
        }
    }

    /// <summary>
    /// Applies one entry per declared name: a queue, a type or a budget. The name ignores case unless the code declares
    /// two names that differ only in case; then it must match one of them exactly.
    /// </summary>
    private static void Each<T>(
        IConfigurationSection section, List<SyncError> errors, IEnumerable<string> declared, Func<string, T?> find, Action<T, IConfigurationSection> apply)
        where T : class
    {
        if (section.Value is not null)
            errors.Add(SyncLaneErrors.SettingValueInvalid(section.Path, Entries));

        var names = declared.ToList();
        foreach (var child in section.GetChildren())
        {
            var alike = names.Where(name => string.Equals(name, child.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            var name = alike.Contains(child.Key, StringComparer.Ordinal) ? child.Key : alike.Count == 1 ? alike[0] : null;

            if (name is not null && find(name) is { } found)
                apply(found, child);
            else
                errors.Add(SyncLaneErrors.SettingNotDeclared(child.Path, names));
        }
    }

    private static void Read<T>(IConfigurationSection section, string expected, Parse<T> parse, Action<T> apply, List<SyncError> errors)
    {
        if (section.Value is { } text && parse(text, out var value))
            apply(value);
        else
            errors.Add(SyncLaneErrors.SettingValueInvalid(section.Path, expected));
    }

    private static bool Words(string text, out string value)
    {
        value = text;
        return true;
    }

    private static bool Whole(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool Spans(string text, out TimeSpan value) =>
        TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out value);

    private static bool Fraction(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
