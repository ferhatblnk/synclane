using System.Text.RegularExpressions;

namespace SyncLane.Http;

/// <summary>A recipe's rate budget, checked: the budget's name, the template of its key, and the headers it reads.</summary>
internal sealed partial class CompiledRate(string budget, Template key, RateHeaders? headers)
{
    private static readonly Template OwnerKey = Template.Parse($"{{{{{Placeholders.OwnerKey}}}}}")!;

    public string Budget => budget;

    /// <summary>The headers that say how many requests are left, or <c>null</c> when the recipe names none.</summary>
    public RateHeaders? Headers => headers;

    /// <summary>The bucket's key for <paramref name="job"/>.</summary>
    public string KeyOf(Job job) => key.Render(_ => job.Owner);

    /// <summary>The rate section, or <c>null</c> when the recipe has none or it has errors.</summary>
    public static CompiledRate? Compile(string recipe, HttpRecipeRate? rate, List<SyncError> errors)
    {
        if (rate is null)
            return null;

        var before = errors.Count;
        if (rate.Budget is null || !Name().IsMatch(rate.Budget))
            errors.Add(HttpErrors.RateInvalid(recipe, "budget", "must be 1 to 100 letters, digits, dots, underscores, colons, slashes or hyphens"));

        var key = OwnerKey;
        if (rate.Key is not null)
        {
            var parsed = string.IsNullOrWhiteSpace(rate.Key) ? null : Template.Parse(rate.Key);
            if (parsed is null)
                errors.Add(HttpErrors.RateInvalid(recipe, "key", "must be text, with placeholders written as {{owner.key}}"));
            else if (parsed.Placeholders.Any(name => name != Placeholders.OwnerKey))
                errors.Add(HttpErrors.RateInvalid(recipe, "key", "may only use {{owner.key}}: a bucket belongs to an account, not to a window"));
            else
                key = parsed;
        }

        var headers = RateHeaders.Compile(recipe, rate.Headers, errors);
        return errors.Count > before ? null : new CompiledRate(rate.Budget!, key, headers);
    }

    [GeneratedRegex("^[A-Za-z0-9._:/-]{1,100}$")]
    private static partial Regex Name();
}
