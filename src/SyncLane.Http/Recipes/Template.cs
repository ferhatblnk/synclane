using System.Text;

namespace SyncLane.Http;

/// <summary>
/// Text with <c>{{name}}</c> placeholders, split once into literal and placeholder parts so that rendering a page is a
/// single pass with no parsing. There are no expressions, filters or nesting: a placeholder is a name, nothing more.
/// </summary>
internal sealed class Template
{
    private const string Open = "{{";
    private const string Close = "}}";

    private readonly (string Text, bool IsPlaceholder)[] _parts;

    private Template((string Text, bool IsPlaceholder)[] parts)
    {
        _parts = parts;
        Placeholders = parts.Where(part => part.IsPlaceholder).Select(part => part.Text).ToArray();
    }

    /// <summary>The placeholder names, in order of appearance.</summary>
    public IReadOnlyList<string> Placeholders { get; }

    /// <summary>Splits <paramref name="text"/>, or returns <c>null</c> when a placeholder is unclosed or empty.</summary>
    public static Template? Parse(string text)
    {
        var parts = new List<(string, bool)>();
        var position = 0;

        while (position < text.Length)
        {
            var open = text.IndexOf(Open, position, StringComparison.Ordinal);
            if (open < 0)
            {
                parts.Add((text[position..], false));
                break;
            }

            var close = text.IndexOf(Close, open + Open.Length, StringComparison.Ordinal);
            if (close < 0)
                return null;

            var name = text[(open + Open.Length)..close].Trim();
            if (name.Length == 0 || name.Contains('{') || name.Contains('}'))
                return null;

            if (open > position)
                parts.Add((text[position..open], false));

            parts.Add((name, true));
            position = close + Close.Length;
        }

        return new Template(parts.ToArray());
    }

    /// <summary>Joins the parts, with each placeholder replaced by <paramref name="value"/> of its name.</summary>
    public string Render(Func<string, string> value)
    {
        if (_parts.Length == 1 && !_parts[0].IsPlaceholder)
            return _parts[0].Text;

        var text = new StringBuilder();
        foreach (var (part, isPlaceholder) in _parts)
            text.Append(isPlaceholder ? value(part) : part);

        return text.ToString();
    }
}
