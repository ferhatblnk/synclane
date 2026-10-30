using SyncLane.Http;

namespace SyncLane.Tests.Http;

public class TemplateTests
{
    [Fact]
    public void Placeholders_are_found_with_or_without_spaces()
    {
        var template = Template.Parse("/shops/{{ owner.key }}/orders?from={{window.start}}")!;

        Assert.Equal(["owner.key", "window.start"], template.Placeholders);
        Assert.Equal("/shops/A/orders?from=B", template.Render(name => name == "owner.key" ? "A" : "B"));
    }

    [Theory]
    [InlineData("{{window.start")]
    [InlineData("{{}}")]
    [InlineData("{{ {a} }}")]
    public void An_unclosed_or_empty_placeholder_is_malformed(string text)
    {
        Assert.Null(Template.Parse(text));
    }

    [Fact]
    public void Text_without_placeholders_is_kept_as_it_is()
    {
        var template = Template.Parse("plain }} text")!;

        Assert.Empty(template.Placeholders);
        Assert.Equal("plain }} text", template.Render(_ => throw new InvalidOperationException()));
    }
}
